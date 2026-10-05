using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

/// <summary>
/// Venta real de punta a punta (S2 / D5, D8, D10): login real, búsqueda del producto sembrado por
/// el bootstrap, alta en el carrito, cobro en efectivo con factura visible y verificación en el
/// Historial de Ventas contra el stack real (Backend.API + PostgreSQL aislada + cliente SIN
/// <c>--e2e</c>). Gateado por <c>E2E_POSTGRES_CONNECTION</c>: local sin variable retorna en
/// silencio; en CI falla cerrado (mismo contrato que el smoke del harness).
/// </summary>
public class FullStackSaleTests : IClassFixture<FullStackFixture>
{
    // Datos fijos del producto que el bootstrap crea por API (E2eApiClient.CreateTestProductAsync):
    // costo USD 10.00 con margen retail 30% -> precio USD 13.00 (RoundPriceUp). El valor en Bs.S se
    // deriva de la tasa vigente que muestra el propio POS (ToBsSCeiling) para no acoplarse a una
    // tasa fija: el backend pisa la del bootstrap con la tasa BCV real (ver WaitForExchangeRateSettlement).
    private const decimal ProductCostUsd = 10.00m;
    private const decimal RetailMarginPercent = 30.00m;

    /// <summary>Nombre visible del método de pago en efectivo sembrado por el backend (PaymentMethodDefaults).</summary>
    private const string CashMethodName = "Efectivo";

    // Ventana del BcvExchangeRateJob del backend: 5 s de delay inicial + scrape ≤ BcvSettings:TimeoutSeconds
    // (15 s en appsettings) + margen. Dentro de esa ventana la tasa queda estable (siguiente ciclo: 120 min).
    private static readonly TimeSpan BcvSettlementWindow = TimeSpan.FromSeconds(25);

    private static readonly Regex InvoiceNumberPattern = new(@"Factura N° (\d+)", RegexOptions.Compiled);
    private static readonly Regex SalesCountPattern = new(@"\((\d+) ventas\)", RegexOptions.Compiled);

    private readonly FullStackFixture _fixture;

    public FullStackSaleTests(FullStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CompleteSale_WithCashPayment_ShowsInvoiceNumberAndMatchesSalesHistory()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var findTimeout = UiaRetry.FindTimeout;

        await _fixture.InitializeAsync();

        try
        {
            var window = _fixture.ClientWindow
                ?? throw new InvalidOperationException("El cliente WPF no se lanzó contra el harness full-stack.");
            var bootstrap = _fixture.Bootstrap
                ?? throw new InvalidOperationException("El bootstrap del harness no produjo estado inicial.");

            // 1) Login real con la clave rotada por el bootstrap.
            TestHelper.EnsureLoggedInWithCredentials(window, _fixture.AdminUsername, _fixture.AdminPassword);
            AssertNoErrorDialogs();

            // 1b) Residuo de corridas previas: el cliente real persiste la venta en curso en
            //     %LocalAppData%\ProyectoPOS\active_sale_recovery.json. Si quedó un snapshot, al
            //     inicializar el POS aparece un confirm modal ("Recuperar venta sin finalizar") que
            //     congela la interacción; se rechaza (No) para arrancar una venta limpia ANTES de
            //     agregar el producto (el add usa lazy-start y quedaría en la venta vieja).
            DismissStaleRecoveryPromptIfPresent(window, findTimeout);
            AssertNoErrorDialogs();

            // 1c) Tasa alineada por UI: el backend sincroniza la tasa BCV real poco después de
            //     arrancar (BcvExchangeRateJob) y el cliente puede quedar con la tasa del bootstrap;
            //     si el cobro se procesa con tasas distintas, el backend ancla a la oficial y
            //     revaloriza la venta, rompiendo la coherencia de montos. Se espera la ventana del
            //     job y se guarda por UI (Admin) la tasa vigente del POS, dejando cliente y backend
            //     con la MISMA referencia antes de vender.
            decimal clientRate = WaitForExchangeRateSettlement(window, bootstrap.ExchangeRate, findTimeout);
            decimal appliedRate = AlignExchangeRateThroughUi(window, clientRate, findTimeout);

            decimal unitPriceUsd = Math.Ceiling(ProductCostUsd * (1m + (RetailMarginPercent / 100m)) * 100m) / 100m;
            decimal expectedUnitPriceBsS = Math.Ceiling(unitPriceUsd * appliedRate * 100m) / 100m;
            decimal expectedCashPaidBsS = Math.Round(expectedUnitPriceBsS, 0, MidpointRounding.AwayFromZero);

            // 2) POS real: búsqueda por SKU único + alta en el carrito. El total del resumen debe
            //    reflejar el precio costo+margen en Bs.S a la tasa vigente (ToBsSCeiling).
            decimal cartTotalBsS = AddFixtureProductToCart(window, bootstrap.ProductSku, bootstrap.ProductName, findTimeout);
            Assert.True(
                cartTotalBsS == expectedUnitPriceBsS,
                $"El total del carrito ({cartTotalBsS}) no coincide con el precio esperado ({expectedUnitPriceBsS}) " +
                $"para la tasa {appliedRate} leída al inicio. {DescribeCart(window, bootstrap.ProductName)}");
            AssertNoErrorDialogs();

            // 3) Cobro real en efectivo: la factura que muestra la UI es el ancla de la verificación
            //    y el monto pagado (Bs.S entero, RoundToCash) debe cubrir el 100% de la venta.
            var checkout = CompleteCashCheckout(window, findTimeout);
            Assert.True(checkout.InvoiceNumber > 0, "El diálogo de éxito mostró un N° de factura inválido.");
            Assert.Equal(expectedCashPaidBsS, checkout.PaidBsS);
            AssertNoErrorDialogs();

            // 4) Historial de Ventas real: la factura localizada por su número debe aparecer con el
            //    total del período igual al monto cobrado que mostró la UI.
            AssertSaleVisibleInHistory(window, checkout.InvoiceNumber, checkout.PaidBsS, findTimeout);
            AssertNoErrorDialogs();
        }
        finally
        {
            await _fixture.TeardownAsync();
        }
    }

    /// <summary>
    /// Rechaza el confirm de recuperación si quedó un snapshot de una corrida anterior. La espera
    /// es acotada por <see cref="UiaRetry.FindTimeout"/>: sin residuo agota el presupuesto sin
    /// encontrar nada y el flujo continúa (no hay otros confirms posibles en este punto).
    /// </summary>
    private static void DismissStaleRecoveryPromptIfPresent(Window window, TimeSpan findTimeout)
    {
        var recoveryPrompt = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var confirm = window.FindFirstDescendant(cf => cf.ByAutomationId("CustomDialogWindow_Confirm"));
                if (confirm == null)
                {
                    return null;
                }

                bool isRecoveryPrompt = confirm
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Any(text => text.Name?.Contains("Recuperar venta sin finalizar", StringComparison.OrdinalIgnoreCase) == true);
                return isRecoveryPrompt ? confirm : null;
            }),
            findTimeout);

        if (recoveryPrompt.Result == null)
        {
            return;
        }

        var declineButton = recoveryPrompt.Result.FindFirstDescendant(cf => cf.ByAutomationId("BtnNo"))?.AsButton()
            ?? throw new InvalidOperationException("El confirm de recuperación apareció sin su botón 'No'.");
        declineButton.Invoke();

        Retry.WhileNotNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId("CustomDialogWindow_Confirm"))),
            findTimeout);
    }

    /// <summary>
    /// Espera acotada a que la tasa del POS quede estable y devuelve el valor vigente. Si ya difiere
    /// de la del bootstrap, el sync BCV del backend ya ocurrió (retorno inmediato); si nunca cambia
    /// (sin internet o tasa oficial == bootstrap), el intento del job igualmente resolvió dentro de
    /// <see cref="BcvSettlementWindow"/> y la tasa permanece estable para el cobro.
    /// </summary>
    private decimal WaitForExchangeRateSettlement(Window window, decimal bootstrapRate, TimeSpan findTimeout)
    {
        var waitBudget = TimeSpan.FromSeconds(Math.Max(findTimeout.TotalSeconds, BcvSettlementWindow.TotalSeconds));

        var settled = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var rate = ReadExchangeRate(window);
                return rate > 0 && rate != bootstrapRate ? (decimal?)rate : null;
            }),
            waitBudget);

        if (settled.Result.HasValue)
        {
            return settled.Result.Value;
        }

        decimal currentRate = ReadExchangeRate(window);
        Assert.True(
            currentRate > 0,
            $"La tasa de cambio del POS no apareció (Pos_ExchangeRateText). {DescribeUi(window)}");
        return currentRate;
    }

    private static decimal ReadExchangeRate(Window window)
    {
        var text = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_ExchangeRateText"))?.Name;
        return ParseUiNumber(text);
    }

    /// <summary>
    /// Alinea la tasa del backend con la del cliente a través de la UI real (vista Tasa de Cambio:
    /// input + Guardar, rol Admin). El guardado hace POST /api/exchange-rate y actualiza la tasa
    /// local, así el cobro no dispara el anclaje por desvío (&gt;10%) del backend.
    /// </summary>
    private decimal AlignExchangeRateThroughUi(Window window, decimal currentRate, TimeSpan findTimeout)
    {
        string desired = currentRate.ToString("0.00", CultureInfo.InvariantCulture);

        NavigateToView(window, "Nav_BtnExchangeRate", findTimeout);

        var rateInput = WaitForElement(window, "ExchangeRate_RateInput", findTimeout)?.AsTextBox()
            ?? throw new InvalidOperationException($"El input de tasa no apareció. {DescribeUi(window)}");

        // La carga de la vista (GET /today) puede pisar el texto: se re-escribe hasta que persista.
        var inputSettled = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                rateInput.Text = desired;
                return string.Equals(rateInput.Text?.Trim(), desired, StringComparison.Ordinal) ? desired : null;
            }),
            findTimeout);

        Assert.True(
            inputSettled.Result != null,
            $"No se pudo fijar la tasa '{desired}' en la vista de Tasa de Cambio. {DescribeUi(window)}");

        var saveButton = WaitForEnabledButton(window, "ExchangeRate_SaveButton", findTimeout)
            ?? throw new InvalidOperationException($"El botón Guardar de la tasa no está habilitado. {DescribeUi(window)}");
        saveButton.Invoke();

        // Confirmación real del guardado en la UI (mensaje de estado del VM).
        var saveConfirmed = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var texts = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Select(text => text.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToArray();

                var error = texts.FirstOrDefault(name => name!.Contains("Error al guardar", StringComparison.OrdinalIgnoreCase));
                if (error != null)
                {
                    throw new InvalidOperationException($"El guardado de la tasa falló en la UI: '{error}'.");
                }

                return texts.FirstOrDefault(name => name!.Contains("guardada correctamente", StringComparison.OrdinalIgnoreCase));
            }),
            findTimeout);

        Assert.True(
            saveConfirmed.Result != null,
            $"No se confirmó el guardado de la tasa en la UI. {DescribeUi(window)}");

        NavigateToView(window, "Nav_BtnPos", findTimeout);
        WaitForElement(window, "Pos_SearchInput", findTimeout);

        return ReadExchangeRate(window);
    }

    /// <summary>
    /// Navega por la barra lateral. En modo real (sin <c>--e2e</c>) el DrawerHost derecho arranca
    /// CERRADO y MaterialDesign deshabilita su contenido para UIA (los tests mock navegan con el
    /// drawer abierto por el flag). Si la navegación estándar falla, se abre el drawer con el
    /// engranaje del POS (mismo comando del shell) y se reintenta; como último recurso se invoca
    /// el botón con InvokePattern.
    /// </summary>
    private void NavigateToView(Window window, string navAutomationId, TimeSpan findTimeout)
    {
        if (TestHelper.NavigateTo(window, navAutomationId))
        {
            return;
        }

        var openMenuButton = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_OpenMenuButton"))?.AsButton();
        if (openMenuButton is { IsEnabled: true })
        {
            openMenuButton.Invoke();
        }

        if (TestHelper.NavigateTo(window, navAutomationId))
        {
            return;
        }

        var button = WaitForElement(window, navAutomationId, findTimeout)?.AsButton()
            ?? throw new InvalidOperationException(
                $"No se encontró el botón de navegación '{navAutomationId}'. {DescribeElement(window, navAutomationId)} {DescribeUi(window)}");
        button.Invoke();
    }

    /// <summary>
    /// POS: escribe el SKU exacto en el buscador y agrega el producto con Enter sobre la primera
    /// sugerencia (<c>SearchKeyboardBehavior</c>). Devuelve el total Bs.S que muestra el resumen.
    /// </summary>
    private decimal AddFixtureProductToCart(Window window, string sku, string productName, TimeSpan findTimeout)
    {
        var searchInput = WaitForElement(window, "Pos_SearchInput", findTimeout)?.AsTextBox()
            ?? throw new InvalidOperationException(
                $"El buscador del POS no apareció tras el login. {DescribeUi(window)}");

        // La búsqueda por SKU exacto devuelve una única sugerencia (el producto del fixture).
        searchInput.Text = sku;

        // Interacción real: con la sugerencia cargada, Enter agrega la PRIMERA (SelectedItem ??
        // Items[0]); si la lista aún no llegó, el atajo es un no-op. Se reintenta hasta que la fila
        // del carrito aparezca (tras agregar, la lista se limpia y el carrito queda visible).
        var cartRow = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var cartGrid = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_CartGrid"));
                if (cartGrid != null)
                {
                    var row = cartGrid.FindFirstDescendant(cf => cf.ByName(productName));
                    if (row != null)
                    {
                        return row;
                    }
                }

                // Si la sugerencia es visible en el árbol UIA se selecciona explícitamente; el
                // Enter del behavior agrega SelectedItem ?? Items[0].
                var suggestion = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_SuggestionsList"))?
                    .FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
                    .FirstOrDefault();
                (suggestion?.AsListBoxItem())?.Select();

                // SendInput va a la ventana en primer plano: se activa la app antes del Enter
                // (best-effort; el testhost puede haber recuperado el foco).
                try
                {
                    window.SetForeground();
                }
                catch (Exception)
                {
                    // Sin activación no se aborta: Focus + Enter puede seguir funcionando.
                }

                searchInput.Focus();
                Keyboard.Press(VirtualKeyShort.RETURN);
                return null;
            }),
            findTimeout);

        Assert.True(
            cartRow.Result != null,
            $"El producto '{productName}' no apareció en el carrito tras el Enter sobre la sugerencia. " +
            $"Buscador='{searchInput.Text}'. {DescribeSuggestions(window)} {DescribeUi(window)}");

        // El total se actualiza por binding: se espera a que el texto deje de ser 0.
        var cartTotal = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var parsed = ParseUiNumber(window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_TotalAmountText"))?.Name);
                return parsed > 0 ? (decimal?)parsed : null;
            }),
            findTimeout);

        Assert.True(
            cartTotal.Result.HasValue,
            $"El total Bs.S del carrito no se actualizó tras agregar el producto. {DescribeUi(window)}");

        return cartTotal.Result!.Value;
    }

    /// <summary>
    /// Modal de cobro: elige Efectivo, captura el monto autocompletado (pago en efectivo al 100%),
    /// agrega el pago, finaliza y devuelve el N° de factura que muestra el diálogo de éxito real
    /// junto con el monto Bs.S cobrado.
    /// </summary>
    private CheckoutOutcome CompleteCashCheckout(Window window, TimeSpan findTimeout)
    {
        var checkoutButton = WaitForEnabledButton(window, "Pos_CheckoutButton", findTimeout)
            ?? throw new InvalidOperationException(
                $"El botón COBRAR del POS no se habilitó con el carrito cargado. {DescribeUi(window)}");
        checkoutButton.Invoke();

        // ComboBox WPF de métodos de pago: la selección debe quedar EFECTIVA en el VM (SelectedItem
        // del combo), no solo en el patrón del ítem; si el patrón no la refleja, se cae a teclado.
        var cashMethod = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var combo = window.FindFirstDescendant(cf => cf.ByAutomationId("Checkout_PaymentMethodCombo"))?.AsComboBox();
                if (combo == null)
                {
                    return null;
                }

                if (IsCashMethodSelected(combo))
                {
                    return combo.SelectedItem;
                }

                var item = combo.Select(CashMethodName);
                if (item != null && IsCashMethodSelected(combo))
                {
                    return combo.SelectedItem;
                }

                // Fallback por teclado (WPF: Down sobre combo cerrado sin selección toma el primero).
                try
                {
                    window.SetForeground();
                }
                catch (Exception)
                {
                    // Activación best-effort.
                }

                combo.Focus();
                Keyboard.Press(VirtualKeyShort.DOWN);
                return IsCashMethodSelected(combo) ? combo.SelectedItem : null;
            }),
            findTimeout);

        Assert.True(
            cashMethod.Result != null,
            $"No se pudo seleccionar el método 'Efectivo' en el modal de cobro. {DescribeCheckoutMethods(window)}");

        // Al elegir el método, el VM autocompleta el monto con el saldo restante redondeado a efectivo.
        var amountFilled = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var text = window.FindFirstDescendant(cf => cf.ByAutomationId("Checkout_AmountInput"))?.AsTextBox()?.Text;
                return ParseUiNumber(text) > 0 ? text : null;
            }),
            findTimeout);

        Assert.True(
            amountFilled.Result != null,
            $"El monto Bs.S del cobro no se autocompletó al elegir 'Efectivo'. {DescribeCheckoutState(window)} {DescribeUi(window)}");

        decimal paidBsS = ParseUiNumber(amountFilled.Result);

        var addPaymentButton = WaitForEnabledButton(window, "Checkout_AddPaymentButton", findTimeout)
            ?? throw new InvalidOperationException(
                $"El botón de agregar pago del modal de cobro no apareció habilitado. {DescribeUi(window)}");
        addPaymentButton.Invoke();

        // COBRAR Y FINALIZAR solo se habilita cuando el pago cubre el 100% (CheckoutViewModel.CanFinalize).
        var finalizeButton = WaitForEnabledButton(window, "Checkout_FinalizeSaleButton", findTimeout)
            ?? throw new InvalidOperationException(
                $"El botón COBRAR Y FINALIZAR no se habilitó: el pago no quedó registrado. " +
                $"{ReadWarningDialog(window)} {DescribeCheckoutState(window)} {DescribeUi(window)}");
        finalizeButton.Invoke();

        // La respuesta real del backend expone el consecutivo en el diálogo de éxito de la UI.
        var invoiceMessage = Retry.WhileNull(
            () => UiaRetry.RetryUia(() => window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(element => element.Name)
                .FirstOrDefault(name => name != null && InvoiceNumberPattern.IsMatch(name))),
            findTimeout);

        Assert.True(
            invoiceMessage.Result != null,
            $"La UI no mostró el N° de factura tras finalizar el cobro. {DescribeUi(window)}");

        var match = InvoiceNumberPattern.Match(invoiceMessage.Result!);
        int invoiceNumber = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

        // Cierre por la acción primaria (no se pulsa "Guardar Recibo" para no abrir el PDF). Si el
        // diálogo se autocerró por su timer de 7 s, el flujo continúa igual: no es un fallo.
        var continueButton = Retry.WhileNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByName("Continuar"))?.AsButton()),
            findTimeout);
        continueButton.Result?.Invoke();

        return new CheckoutOutcome(invoiceNumber, paidBsS);
    }

    /// <summary>
    /// Historial de Ventas: filtro de control (término inexistente -> 0 ventas) y localización
    /// determinista por el N° de factura capturado de la UI (-> 1 venta) con el total del período
    /// igual al monto cobrado que mostró el POS.
    /// </summary>
    private void AssertSaleVisibleInHistory(Window window, int invoiceNumber, decimal expectedPaidBsS, TimeSpan findTimeout)
    {
        NavigateToView(window, "Nav_BtnSalesHistory", findTimeout);

        var historySearch = WaitForElement(window, "SalesHistory_SearchInput", findTimeout)?.AsTextBox()
            ?? throw new InvalidOperationException(
                $"El buscador del Historial de Ventas no apareció. {DescribeUi(window)}");

        historySearch.Text = "e2e-sin-coincidencias";
        WaitForSalesCount(window, 0, findTimeout);

        historySearch.Text = invoiceNumber.ToString(CultureInfo.InvariantCulture);
        WaitForSalesCount(window, 1, findTimeout);

        var periodTotal = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var parsed = ParseUiNumber(window.FindFirstDescendant(cf => cf.ByAutomationId("SalesHistory_PeriodTotal"))?.Name);
                return parsed > 0 ? (decimal?)parsed : null;
            }),
            findTimeout);

        Assert.True(
            periodTotal.Result.HasValue,
            $"El 'Total del Período' del historial no se actualizó con la factura #{invoiceNumber:D6}. {DescribeUi(window)}");
        Assert.Equal(expectedPaidBsS, periodTotal.Result!.Value);
    }

    private void WaitForSalesCount(Window window, int expectedCount, TimeSpan findTimeout)
    {
        var summary = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var name = window.FindFirstDescendant(cf => cf.ByAutomationId("SalesHistory_PageSummary"))?.Name;
                if (name == null)
                {
                    return null;
                }

                var match = SalesCountPattern.Match(name);
                return match.Success && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == expectedCount
                    ? name
                    : null;
            }),
            findTimeout);

        Assert.True(
            summary.Result != null,
            $"El Historial de Ventas no alcanzó {expectedCount} venta(s) para el filtro aplicado. {DescribeUi(window)}");
    }

    private static AutomationElement? WaitForElement(Window window, string automationId, TimeSpan timeout)
    {
        var result = Retry.WhileNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))),
            timeout);
        return result.Result;
    }

    private static Button? WaitForEnabledButton(Window window, string automationId, TimeSpan timeout)
    {
        var result = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var button = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))?.AsButton();
                return button is { IsEnabled: true } ? button : null;
            }),
            timeout);
        return result.Result;
    }

    /// <summary>
    /// Extrae el primer número de un texto de la UI y lo parsea de forma tolerante a cultura
    /// ("Bs.S 650.00", "11,328.00", "650,00", "Tasa: 871.37 Bs/$"). El regex arranca en un dígito
    /// para no capturar el punto de la etiqueta "Bs.S" como separador decimal.
    /// </summary>
    private static decimal ParseUiNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0m;
        }

        var match = Regex.Match(text, @"\d[\d.,]*");
        if (!match.Success)
        {
            return 0m;
        }

        var digits = match.Value;

        // "11,328.00" -> la coma separa miles; "650,00" -> la coma es decimal.
        digits = digits.Contains(',') && digits.Contains('.')
            ? digits.Replace(",", string.Empty)
            : digits.Replace(',', '.');

        return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
    }

    private void AssertNoErrorDialogs()
    {
        var dialogs = _fixture.FindErrorDialogs();
        Assert.True(
            dialogs.Length == 0,
            $"Diálogos de error inesperados durante el flujo de venta real: {string.Join(" | ", dialogs)}");
    }

    private static bool IsCashMethodSelected(ComboBox combo)
    {
        try
        {
            return string.Equals(combo.SelectedItem?.Text, CashMethodName, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Texto del warning modal visible (si hay), para diagnóstico de fallos del cobro.</summary>
    private static string ReadWarningDialog(Window window)
    {
        try
        {
            var dialog = window.FindFirstDescendant(cf => cf.ByAutomationId("CustomDialogWindow_Warning"));
            if (dialog == null)
            {
                return "sin warning modal";
            }

            var texts = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(text => text.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name));
            return $"warning modal: [{string.Join(" | ", texts)}]";
        }
        catch (Exception ex)
        {
            return $"lectura del warning falló: {ex.Message}";
        }
    }

    private static string DescribeElement(Window window, string automationId)
    {
        try
        {
            var element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            return element == null
                ? $"'{automationId}': ausente"
                : $"'{automationId}': IsEnabled={element.IsEnabled}, IsOffscreen={element.IsOffscreen}, IsAvailable={element.IsAvailable}";
        }
        catch (Exception ex)
        {
            return $"'{automationId}': lectura de estado falló ({ex.Message})";
        }
    }

    private static string DescribeSuggestions(Window window)
    {
        try
        {
            var list = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_SuggestionsList"));
            if (list == null)
            {
                return "Pos_SuggestionsList no visible desde la ventana principal.";
            }

            var items = list.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
                .Select(item => item.Name)
                .ToArray();
            return $"Sugerencias visibles: [{string.Join(" | ", items)}].";
        }
        catch (Exception ex)
        {
            return $"DescribeSuggestions falló: {ex.Message}.";
        }
    }

    private static string DescribeCart(Window window, string productName)
    {
        try
        {
            var rate = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_ExchangeRateText"))?.Name;
            var total = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_TotalAmountText"))?.Name;
            var grid = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_CartGrid"));
            var texts = grid?
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(text => text.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray() ?? Array.Empty<string>();
            return $"Tasa='{rate}', Total='{total}', Textos del carrito de '{productName}': [{string.Join(" | ", texts)}].";
        }
        catch (Exception ex)
        {
            return $"DescribeCart falló: {ex.Message}.";
        }
    }

    private static string DescribeCheckoutState(Window window)
    {
        try
        {
            var amount = window.FindFirstDescendant(cf => cf.ByAutomationId("Checkout_AmountInput"))?.AsTextBox()?.Text;
            var texts = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(text => text.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(25);
            return $"AmountInput='{amount ?? "(null)"}'. Textos del modal: [{string.Join(" | ", texts)}].";
        }
        catch (Exception ex)
        {
            return $"No se pudo leer el estado del modal de cobro: {ex.Message}.";
        }
    }

    private static string DescribeCheckoutMethods(Window window)
    {
        try
        {
            var combo = window.FindFirstDescendant(cf => cf.ByAutomationId("Checkout_PaymentMethodCombo"))?.AsComboBox();
            var items = combo?.Items.Select(item => item.Text).ToArray() ?? Array.Empty<string>();
            return $"Métodos de pago disponibles en el modal: [{string.Join(", ", items)}].";
        }
        catch (Exception ex)
        {
            return $"No se pudo enumerar los métodos de pago del modal: {ex.Message}.";
        }
    }

    /// <summary>Diagnóstico para fallos: diálogos de error visibles + ids de automatización presentes.</summary>
    private string DescribeUi(Window window)
    {
        var dialogs = _fixture.FindErrorDialogs();
        var errorInfo = dialogs.Length == 0
            ? "sin diálogos de error visibles"
            : $"diálogos de error: {string.Join(" | ", dialogs)}";

        string ids;
        try
        {
            // ValueOrDefault: el árbol incluye peers que no soportan la propiedad AutomationId.
            ids = string.Join(", ", window.FindAllDescendants()
                .Select(element => element.Properties.AutomationId.ValueOrDefault)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .Take(40));
        }
        catch (Exception ex)
        {
            ids = $"(la enumeración UIA falló: {ex.Message})";
        }

        return $"{errorInfo}. Ids visibles: {ids}";
    }

    private readonly record struct CheckoutOutcome(int InvoiceNumber, decimal PaidBsS);
}
