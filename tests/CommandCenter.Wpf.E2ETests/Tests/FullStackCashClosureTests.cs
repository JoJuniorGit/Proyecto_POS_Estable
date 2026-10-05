using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

/// <summary>
/// Caja y Cierre Diario reales de punta a punta (S3 / D9): ciclo de vida de la sesión de caja
/// abierta por la venta real, movimiento manual por el diálogo real de CASH IN, arqueo/cierre
/// diario por la UI y verificación del estado resultante contra el MISMO stack real
/// (Backend.API + PostgreSQL aislada + cliente SIN <c>--e2e</c>).
/// </summary>
/// <remarks>
/// Precondiciones de negocio descubiertas (Phase 4, T5):
/// <list type="bullet">
/// <item>La sesión de caja no tiene botón de apertura en la UI: nace del cobro real
/// (<c>SalesService.Checkout</c> → <c>GetOrCreateActiveSessionAsync</c>), que además registra el
/// ingreso físico del pago en efectivo con arrastre del saldo del último cierre.</item>
/// <item>El cierre diario es el camino real de cierre/rollover de la sesión: al confirmar, el
/// servidor registra el arqueo y ejecuta <c>RolloverSessionAfterClosureAsync</c> (cierra la sesión
/// activa con el saldo actual y abre una nueva con ese arrastre).</item>
/// <item>El esperado del cierre se computa por ventas de la ventana de la sesión activa (menos
/// vueltos en efectivo); un ingreso manual de caja NO entra en el esperado (solo en el saldo).</item>
/// <item>La grilla de montos declarados es un <c>PerformantDataGrid</c> con AutomationPeer null
/// (ver <c>Desktop.Client/Controls/PerformantDataGrid.cs</c>): UIA no puede escribir los montos
/// declarados. El camino feliz acotado por D9 declara 0 y verifica que el faltante mostrado sea
/// exactamente el esperado del servidor.</item>
/// </list>
/// Gateado por <c>E2E_POSTGRES_CONNECTION</c>: local sin variable retorna en silencio; en CI sin
/// variable falla cerrado (mismo contrato que el resto del harness).
/// </remarks>
public class FullStackCashClosureTests : IClassFixture<FullStackFixture>
{
    // Datos fijos del producto que el bootstrap crea por API (E2eApiClient.CreateTestProductAsync):
    // costo USD 10.00 con margen retail 30% -> precio USD 13.00 (RoundPriceUp).
    private const decimal ProductCostUsd = 10.00m;
    private const decimal RetailMarginPercent = 30.00m;

    /// <summary>Nombre visible del método de pago en efectivo sembrado por el backend (PaymentMethodDefaults).</summary>
    private const string CashMethodName = "Efectivo";

    /// <summary>Monto entero controlado del CASH IN real (el efectivo solo acepta montos enteros).</summary>
    private const decimal CashInAmountBsS = 100m;

    // Igual a SystemSettings:MinimumClientVersion (Backend.API/appsettings.json): el
    // VersionCheckMiddleware exige el header en rutas /api/* que no sean auth/pairing.
    private const string ClientVersion = "1.0.0";

    // Ventana del BcvExchangeRateJob del backend (5 s de delay inicial + scrape ≤ BcvSettings:TimeoutSeconds
    // + margen). Dentro de esa ventana la tasa queda estable (siguiente ciclo: 120 min).
    private static readonly TimeSpan BcvSettlementWindow = TimeSpan.FromSeconds(25);

    private static readonly Regex InvoiceNumberPattern = new(@"Factura N° (\d+)", RegexOptions.Compiled);

    private readonly FullStackFixture _fixture;

    public FullStackCashClosureTests(FullStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CashDrawerSession_AndDailyClosure_ReflectRealServerState()
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

            using var dialogAutomation = new UIA3Automation();

            // 1) Login real + higiene de arranque (recovery de venta en curso y tasa), mismo patrón
            //    que el flujo de venta real.
            EnsureLoggedInWithBoundedRetry(window, findTimeout);
            AssertNoErrorDialogs();
            DismissStaleRecoveryPromptIfPresent(window, findTimeout);
            AssertNoErrorDialogs();

            // 1b) Tasa alineada por UI ANTES de cualquier navegación al drawer (mismo orden que el
            //     flujo de venta verificado): el backend sincroniza la tasa BCV real poco después de
            //     arrancar y el cobro/cierre deben usar la MISMA referencia que el servidor.
            decimal clientRate = WaitForExchangeRateSettlement(window, bootstrap.ExchangeRate, findTimeout);
            decimal appliedRate = AlignExchangeRateThroughUi(window, clientRate, findTimeout);

            decimal unitPriceUsd = Math.Ceiling(ProductCostUsd * (1m + (RetailMarginPercent / 100m)) * 100m) / 100m;
            decimal expectedUnitPriceBsS = Math.Ceiling(unitPriceUsd * appliedRate * 100m) / 100m;

            // 1c) Estado inicial real de la caja: base aislada nueva -> sin sesión activa. La UI lo
            //     refleja con saldo 0 y las acciones de caja colapsadas (Visibility=IsSessionActive).
            NavigateToView(window, "Nav_BtnCashDrawer", findTimeout);
            var initialBalanceElement = WaitForElement(window, "CashDrawer_BalanceText", findTimeout)
                ?? throw new InvalidOperationException($"La vista de caja no expuso el saldo. {DescribeUi(window)}");
            Assert.True(
                ParseUiAmount(initialBalanceElement.Name) == 0m,
                $"La caja arrancó con saldo distinto de cero sin sesión: '{initialBalanceElement.Name}'. {DescribeUi(window)}");
            Assert.True(
                IsElementHidden(window, "CashDrawer_CashInButton"),
                $"CASH IN debería estar oculto sin sesión activa. {DescribeUi(window)}");

            // 2) Venta real en efectivo: es el alimentador natural que abre la sesión de caja
            //    (GetOrCreateActiveSessionAsync en el cobro) y registra el ingreso físico.
            NavigateToView(window, "Nav_BtnPos", findTimeout);
            WaitForElement(window, "Pos_SearchInput", findTimeout);

            decimal cartTotalBsS = AddFixtureProductToCart(window, bootstrap.ProductSku, bootstrap.ProductName, findTimeout);
            Assert.True(
                cartTotalBsS == expectedUnitPriceBsS,
                $"El total del carrito ({cartTotalBsS}) no coincide con el precio esperado ({expectedUnitPriceBsS}) para la tasa {appliedRate}.");

            var checkout = await CompleteCashCheckout(window, dialogAutomation, findTimeout);
            Assert.True(checkout.InvoiceNumber > 0, "El diálogo de éxito mostró un N° de factura inválido.");
            AssertNoErrorDialogs();

            // 3) La caja refleja la sesión real abierta por la venta: el KPI de ingresos muestra el
            //    efectivo cobrado (entero, por eso la comparación es exacta contra el monto capturado).
            NavigateToView(window, "Nav_BtnCashDrawer", findTimeout);
            WaitForEnabledButton(window, "CashDrawer_CashInButton", findTimeout);
            decimal balanceAfterSale = WaitForPositiveAmountText(window, "CashDrawer_BalanceText", findTimeout);
            decimal incomeAfterSale = ReadAmountText(window, "CashDrawer_TotalIncomeText");
            Assert.True(
                incomeAfterSale == checkout.PaidBsS,
                $"El total de ingresos de caja ({incomeAfterSale}) no coincide con el efectivo cobrado ({checkout.PaidBsS}). {DescribeUi(window)}");

            // 4) Movimiento manual real por el diálogo de CASH IN: monto entero controlado para que
            //    el saldo exhibido (0 decimales) crezca exactamente en ese monto.
            var cashInButton = WaitForEnabledButton(window, "CashDrawer_CashInButton", findTimeout)
                ?? throw new InvalidOperationException($"El botón CASH IN no se habilitó con la sesión abierta. {DescribeUi(window)}");
            cashInButton.Invoke();
            CompleteCashIn(window, findTimeout);

            WaitForExactAmountText(window, "CashDrawer_BalanceText", balanceAfterSale + CashInAmountBsS, findTimeout);
            decimal balanceAfterCashIn = ReadAmountText(window, "CashDrawer_BalanceText");
            decimal incomeAfterCashIn = ReadAmountText(window, "CashDrawer_TotalIncomeText");
            Assert.True(
                incomeAfterCashIn == checkout.PaidBsS + CashInAmountBsS,
                $"Los ingresos de caja ({incomeAfterCashIn}) no reflejan el CASH IN de {CashInAmountBsS} sobre {checkout.PaidBsS}. {DescribeUi(window)}");
            AssertNoErrorDialogs();

            // 5) Cierre Diario real: la vista carga los esperados del servidor para la ventana de la
            //    sesión activa. Sin arqueo declarado (grilla sin peer UIA, ver remarks) el declarado
            //    es 0 y la diferencia mostrada es el FALTANTE por el esperado del servidor.
            NavigateToView(window, "Nav_BtnDailyClosure", findTimeout);
            WaitForElement(window, "DailyClosure_ConfirmButton", findTimeout);
            decimal expectedBeforeClosure = WaitForPositiveAmountText(window, "DailyClosure_ExpectedTotalText", findTimeout);
            decimal actualBeforeClosure = ReadAmountText(window, "DailyClosure_ActualTotalText");
            string statusBeforeClosure = WaitForStatusText(window, "DailyClosure_DifferenceStatusLabel", "FALTANTE", findTimeout);
            Assert.True(actualBeforeClosure == 0m, $"El total declarado inicial debería ser 0: {actualBeforeClosure}. {DescribeUi(window)}");
            Assert.True(
                ParseUiAmount(statusBeforeClosure) == -expectedBeforeClosure,
                $"La diferencia mostrada ('{statusBeforeClosure}') no es el declarado (0) menos el esperado ({expectedBeforeClosure}).");

            // 5b) Coherencia contra el estado real del servidor en la MISMA sesión: el esperado del
            //     cierre (solo ventas) coincide con el saldo de caja menos el CASH IN manual, y la UI
            //     debe estar mostrando exactamente ese esperado.
            using var api = await CreateAuthenticatedApiClientAsync();
            var sessionBeforeClosure = await GetActiveSessionAsync(api)
                ?? throw new InvalidOperationException("La API no reporta sesión de caja activa antes del cierre.");
            decimal serverBalanceBeforeClosure = await GetCurrentBalanceAsync(api, sessionBeforeClosure.Id);
            decimal serverExpectedBeforeClosure = await GetExpectedTotalsTotalAsync(api);
            Assert.True(
                serverExpectedBeforeClosure == serverBalanceBeforeClosure - CashInAmountBsS,
                $"Esperado del servidor ({serverExpectedBeforeClosure}) != saldo del servidor ({serverBalanceBeforeClosure}) - CASH IN ({CashInAmountBsS}).");
            Assert.True(
                expectedBeforeClosure == serverExpectedBeforeClosure,
                $"El esperado de la UI ({expectedBeforeClosure}) != esperado del servidor ({serverExpectedBeforeClosure}).");

            var confirmButton = WaitForEnabledButton(window, "DailyClosure_ConfirmButton", findTimeout)
                ?? throw new InvalidOperationException($"El botón de confirmar cierre no está habilitado. {DescribeUi(window)}");
            confirmButton.Invoke();
            ConfirmAndDismissClosureDialogs(dialogAutomation, window, findTimeout);

            // 6) Estado resultante del cierre en la UI: el servidor cerró la sesión y abrió una nueva
            //    con el saldo arrastrado; la ventana de esperados del nuevo turno queda en cero.
            WaitForStatusText(window, "DailyClosure_DifferenceStatusLabel", "CUADRADO EXACTO", findTimeout);
            WaitForExactAmountText(window, "DailyClosure_ExpectedTotalText", 0m, findTimeout);

            NavigateToView(window, "Nav_BtnCashDrawer", findTimeout);
            WaitForExactAmountText(window, "CashDrawer_BalanceText", balanceAfterCashIn, findTimeout);
            decimal incomeAfterClosure = ReadAmountText(window, "CashDrawer_TotalIncomeText");
            Assert.True(
                incomeAfterClosure == 0m,
                $"El nuevo turno no debe arrastrar ingresos del turno cerrado: {incomeAfterClosure}. {DescribeUi(window)}");
            AssertNoErrorDialogs();

            // 7) Verificación server-side del rollover: sesión nueva (la anterior quedó cerrada) con
            //    arrastre exacto del saldo físico del momento del cierre.
            var sessionAfterClosure = await GetActiveSessionAsync(api)
                ?? throw new InvalidOperationException("La API no reporta sesión de caja activa después del cierre.");
            Assert.True(
                sessionAfterClosure.Id != sessionBeforeClosure.Id,
                $"El cierre no rotó la sesión de caja: sigue activa la sesión #{sessionBeforeClosure.Id}.");
            Assert.True(
                sessionAfterClosure.OpeningBalanceLocal == serverBalanceBeforeClosure,
                $"El arrastre de la nueva sesión ({sessionAfterClosure.OpeningBalanceLocal}) != saldo físico previo al cierre ({serverBalanceBeforeClosure}).");
        }
        finally
        {
            await _fixture.TeardownAsync();
        }
    }

    /// <summary>
    /// Rechaza el confirm de recuperación si quedó un snapshot de una corrida anterior: la venta
    /// en curso persistida (<c>%LocalAppData%\ProyectoPOS\active_sale_recovery.json</c>) bloquea el
    /// POS con un modal. La espera es acotada: sin residuo agota el presupuesto sin encontrar nada.
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

        var declineButton = WaitForDialogButton(recoveryPrompt.Result, "BtnNo", "No", findTimeout)
            ?? throw new InvalidOperationException("El confirm de recuperación apareció sin su botón 'No'.");
        declineButton.Invoke();

        Retry.WhileNotNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId("CustomDialogWindow_Confirm"))),
            findTimeout);
    }

    /// <summary>
    /// Login real con reintentos acotados. El helper compartido escribe usuario por ValuePattern y
    /// la clave por el peer del PasswordBox (con fallback de teclado): una pérdida transitoria de
    /// foco o un hipo de UIA deja la vista a medio exponer y el intento falla (401 o control
    /// ausente), aunque las credenciales del harness sean correctas. Cada reintento re-escribe ambos
    /// campos con la app en primer plano; un fallo real de credenciales vuelve a fallar en el último
    /// intento y el error se propaga sin enmascararse.
    /// </summary>
    private void EnsureLoggedInWithBoundedRetry(Window window, TimeSpan findTimeout)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                WaitForStableLoginView(window);
                TestHelper.EnsureLoggedInWithCredentials(window, _fixture.AdminUsername, _fixture.AdminPassword);
                return;
            }
            catch (InvalidOperationException) when (attempt < maxAttempts && IsLoginViewVisible(window, findTimeout))
            {
                try
                {
                    window.SetForeground();
                }
                catch (Exception)
                {
                    // Activación best-effort: el reintento del helper puede resolver igual.
                }

                Thread.Sleep(500);
            }
        }
    }

    /// <summary>
    /// Espera una vista de login ESTABLE antes de delegar en el helper compartido: el arranque real
    /// purga el token resguardado (401) y reconecta SignalR, lo que puede reconstruir la vista y
    /// dejar el botón de envío fuera del árbol UIA por un instante. Se exige presencia continua del
    /// botón habilitado durante ~1.5 s (presupuesto acotado de 20 s) para no competir con ese churn.
    /// </summary>
    private static void WaitForStableLoginView(Window window)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        DateTime? stableSince = null;
        while (DateTime.UtcNow < deadline)
        {
            bool submitReady = UiaRetry.RetryUia(() =>
                window.FindFirstDescendant(cf => cf.ByAutomationId("Login_SubmitButton"))?.AsButton())
                is { IsEnabled: true };

            if (submitReady)
            {
                stableSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - stableSince.Value >= TimeSpan.FromSeconds(1.5))
                {
                    return;
                }
            }
            else
            {
                stableSince = null;
            }

            Thread.Sleep(200);
        }
    }

    private static bool IsLoginViewVisible(Window window, TimeSpan findTimeout)
    {
        var login = Retry.WhileNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId("Login_Username"))),
            TimeSpan.FromSeconds(Math.Min(findTimeout.TotalSeconds, 3)));
        return login.Result != null;
    }

    /// <summary>
    /// Busca el mensaje de factura del diálogo de éxito real: primero en los elementos de nivel
    /// superior del proceso (el diálogo de éxito es una ventana propia con Owner; el filtro por
    /// ControlType.Window de FlaUI puede excluir ventanas emergentes WPF) y como respaldo bajo el
    /// árbol de la principal. El diálogo se autocierra a los 7 s, así que la ventana de espera debe
    /// cubrir su aparición tras un cobro lento.
    /// </summary>
    private static string? FindInvoiceMessage(Window window, IReadOnlyList<AutomationElement> topLevelElements)
    {
        foreach (var topLevel in topLevelElements)
        {
            string? message = FindInvoiceMessageIn(topLevel);
            if (message != null)
            {
                return message;
            }
        }

        return FindInvoiceMessageIn(window);
    }

    private static string? FindInvoiceMessageIn(AutomationElement root)
    {
        try
        {
            return root
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(element => element.Name)
                .FirstOrDefault(name => name != null && InvoiceNumberPattern.IsMatch(name));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Hijos de nivel superior del proceso (sin filtro de ControlType): los diálogos WPF con
    /// <c>AllowsTransparency</c>/<c>WindowStyle=None</c> pueden no reportar ControlType.Window, y el
    /// filtro de FlaUI los dejaría fuera.
    /// </summary>
    private AutomationElement[] GetProcessTopLevelElements(UIA3Automation automation)
    {
        try
        {
            var app = _fixture.App;
            if (app == null)
            {
                return Array.Empty<AutomationElement>();
            }

            return automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(app.ProcessId));
        }
        catch (Exception)
        {
            return Array.Empty<AutomationElement>();
        }
    }

    /// <summary>Registra (deduplicado por descripción) cada conjunto distinto visto durante una espera.</summary>
    private static void RecordWindowSightings(List<string> sightings, AutomationElement[] elements)
    {
        var description = elements.Length == 0
            ? "(ninguna)"
            : string.Join(", ", elements.Select(DescribeElementShort));
        if (sightings.Count == 0 || !sightings[^1].EndsWith(description, StringComparison.Ordinal))
        {
            sightings.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {description}");
        }
    }

    private static string DescribeElementShort(AutomationElement element)
    {
        string controlType;
        try
        {
            controlType = element.ControlType.ToString();
        }
        catch (Exception)
        {
            controlType = "?";
        }

        string automationId;
        try
        {
            automationId = element.Properties.AutomationId.ValueOrDefault ?? "-";
        }
        catch (Exception)
        {
            automationId = "?";
        }

        string name;
        try
        {
            name = element.Name ?? "-";
        }
        catch (Exception)
        {
            name = "?";
        }

        return $"{controlType}/{automationId}/{name}";
    }

    private static string FormatSightings(IReadOnlyList<string> sightings)
        => sightings.Count == 0
            ? "Ventanas observadas: ninguna."
            : $"Ventanas observadas: {string.Join(" ;; ", sightings)}.";

    /// <summary>
    /// Presupuesto de espera del N° de factura: el triple del timeout de búsqueda (mínimo 45 s)
    /// porque el diálogo de éxito aparece recién cuando termina el cobro real y se autocierra a
    /// los 7 s; un cobro lento bajo carga no debe consumir el presupuesto antes de que exista.
    /// </summary>
    private static TimeSpan InvoiceWaitBudget(TimeSpan findTimeout)
        => TimeSpan.FromSeconds(Math.Max(findTimeout.TotalSeconds * 3, 45));

    /// <summary>
    /// Presupuesto del info de éxito del cierre: el POST serializable del servidor incluye arqueo,
    /// rollover de sesión y escritura de comprobantes antes de responder, por lo que puede superar
    /// el timeout de búsqueda estándar bajo carga. Acotado a 3x el timeout (mínimo 30 s).
    /// </summary>
    private static TimeSpan ClosureDialogBudget(TimeSpan findTimeout)
        => TimeSpan.FromSeconds(Math.Max(findTimeout.TotalSeconds * 3, 30));

    /// <summary>
    /// Espera acotada a que la tasa del POS quede estable y devuelve el valor vigente. Si ya difiere
    /// de la del bootstrap, el sync BCV del backend ya ocurrió (retorno inmediato); si nunca cambia,
    /// el intento del job igualmente resolvió dentro de la ventana y la tasa queda estable.
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
        Assert.True(currentRate > 0, $"La tasa de cambio del POS no apareció (Pos_ExchangeRateText). {DescribeUi(window)}");
        return currentRate;
    }

    private static decimal ReadExchangeRate(Window window)
        => ParseUiAmount(window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_ExchangeRateText"))?.Name);

    /// <summary>
    /// Alinea la tasa del backend con la del cliente por la UI real (vista Tasa de Cambio: input +
    /// Guardar, rol Admin), dejando cliente y backend con la MISMA referencia antes de cobrar/cerrar.
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

        Assert.True(inputSettled.Result != null, $"No se pudo fijar la tasa '{desired}'. {DescribeUi(window)}");

        var saveButton = WaitForEnabledButton(window, "ExchangeRate_SaveButton", findTimeout)
            ?? throw new InvalidOperationException($"El botón Guardar de la tasa no está habilitado. {DescribeUi(window)}");
        saveButton.Invoke();

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

        Assert.True(saveConfirmed.Result != null, $"No se confirmó el guardado de la tasa en la UI. {DescribeUi(window)}");

        NavigateToView(window, "Nav_BtnPos", findTimeout);
        WaitForElement(window, "Pos_SearchInput", findTimeout);

        return ReadExchangeRate(window);
    }

    /// <summary>
    /// Navega por la barra lateral. En modo real el DrawerHost derecho arranca CERRADO y
    /// MaterialDesign deshabilita su contenido para UIA; si la navegación estándar falla se abre con
    /// el engranaje del POS y se reintenta invocar el botón dentro del presupuesto (el drawer puede
    /// estar en transición y alternar el estado habilitado del contenido).
    /// </summary>
    private void NavigateToView(Window window, string navAutomationId, TimeSpan findTimeout)
    {
        if (TestHelper.NavigateTo(window, navAutomationId))
        {
            return;
        }

        var navigated = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var openMenuButton = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_OpenMenuButton"))?.AsButton();
                if (openMenuButton is { IsEnabled: true })
                {
                    try
                    {
                        openMenuButton.Invoke();
                    }
                    catch (FlaUI.Core.Exceptions.ElementNotEnabledException)
                    {
                        return null;
                    }
                }

                var button = window.FindFirstDescendant(cf => cf.ByAutomationId(navAutomationId))?.AsButton();
                if (button is not { IsEnabled: true })
                {
                    return null;
                }

                try
                {
                    button.Invoke();
                    return button;
                }
                catch (FlaUI.Core.Exceptions.ElementNotEnabledException)
                {
                    // Transición del drawer: el presupuesto del Retry decide si se agota.
                    return null;
                }
            }),
            findTimeout);

        Assert.True(
            navigated.Result != null,
            $"No se pudo navegar a '{navAutomationId}' por la barra lateral. {DescribeUi(window)}");
    }

    /// <summary>
    /// POS: escribe el SKU exacto en el buscador y agrega el producto commiteando la primera
    /// sugerencia de forma UIA-first: un click real sobre el ítem del popup dispara el
    /// <c>PreviewMouseLeftButtonDown</c> del template (AddSelectedSuggestionCommand). El Enter del
    /// behavior queda solo como respaldo cuando el ítem no es accionable por UIA. Se despacha UN
    /// único commit por espera: despachado el click, nunca se pulsa Enter (el alta no se duplica).
    /// Devuelve el total Bs.S que muestra el resumen.
    /// </summary>
    private decimal AddFixtureProductToCart(Window window, string sku, string productName, TimeSpan findTimeout)
    {
        var searchInput = WaitForElement(window, "Pos_SearchInput", findTimeout)?.AsTextBox()
            ?? throw new InvalidOperationException($"El buscador del POS no apareció tras el login. {DescribeUi(window)}");

        searchInput.Text = sku;

        // Un solo commit por espera: una vez despachada la interacción que agrega el producto
        // (click UIA o Enter de respaldo), solo se observa su efecto (la fila del carrito) dentro
        // del presupuesto; nunca se re-despacha, por lo que el alta no puede duplicarse.
        bool commitDispatched = false;
        using var automation = new UIA3Automation();

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

                if (commitDispatched)
                {
                    return null;
                }

                var suggestionsList = FindSuggestionsList(window, automation);
                if (suggestionsList == null)
                {
                    // La búsqueda todavía no expuso el popup: no hay nada que commitear aún.
                    return null;
                }

                var suggestion = suggestionsList
                    .FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
                    .FirstOrDefault();
                if (suggestion != null)
                {
                    // UIA-first: el click real sobre el ítem dispara el PreviewMouseLeftButtonDown
                    // del template -> AddSelectedSuggestionCommand. El peer WPF del ListBoxItem no
                    // implementa InvokePattern (solo SelectionItem/ScrollItem), así que el click es
                    // la interacción de usuario equivalente. Select() solo deja SelectedItem listo
                    // (no commitea: OnSelectedSuggestionChanged es un no-op).
                    bool clicked = false;
                    try
                    {
                        suggestion.AsListBoxItem().Select();
                        suggestion.Click();
                        clicked = true;
                    }
                    catch (FlaUI.Core.Exceptions.ElementNotAvailableException)
                    {
                        // La lista se refrescó entre el find y el click: sin input despachado.
                    }
                    catch (FlaUI.Core.Exceptions.NoClickablePointException)
                    {
                        // Ítem sin punto clickeable (animación/reflow del popup): sin input despachado.
                    }

                    if (clicked)
                    {
                        commitDispatched = true;
                        return null;
                    }
                }

                // Respaldo por teclado: la lista está abierta pero su ítem no es accionable por UIA
                // (o el click no despachó input). El Enter del behavior agrega SelectedItem ??
                // Items[0]; el Win32Exception(5) de SendInput lo reintenta UiaRetry como falla
                // transitoria de foco/inyección.
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
                commitDispatched = true;
                return null;
            }),
            findTimeout);

        Assert.True(
            cartRow.Result != null,
            $"El producto '{productName}' no apareció en el carrito tras commitear la sugerencia. " +
            $"Buscador='{searchInput.Text}'. {DescribeUi(window)}");

        var cartTotal = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var parsed = ParseUiAmount(window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_TotalAmountText"))?.Name);
                return parsed > 0 ? (decimal?)parsed : null;
            }),
            findTimeout);

        Assert.True(cartTotal.Result.HasValue, $"El total Bs.S del carrito no se actualizó tras agregar el producto. {DescribeUi(window)}");
        return cartTotal.Result!.Value;
    }

    /// <summary>
    /// Lista de sugerencias del POS localizable por UIA. El popup de WPF vive en su propia ventana
    /// (PopupRoot), así que puede no ser descendiente de la principal: se busca primero bajo la
    /// ventana y, si no está, entre los hijos de nivel superior del proceso (mismo criterio que los
    /// diálogos del harness). Se ignora una lista fuera de pantalla (popup residual de otra
    /// búsqueda).
    /// </summary>
    private static AutomationElement? FindSuggestionsList(Window window, UIA3Automation automation)
    {
        var list = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_SuggestionsList"));
        if (list != null && !IsOffscreenSafe(list))
        {
            return list;
        }

        int processId = window.Properties.ProcessId.ValueOrDefault;
        foreach (var topLevel in automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(processId)))
        {
            list = topLevel.FindFirstDescendant(cf => cf.ByAutomationId("Pos_SuggestionsList"));
            if (list != null && !IsOffscreenSafe(list))
            {
                return list;
            }
        }

        return null;
    }

    private static bool IsOffscreenSafe(AutomationElement element)
    {
        try
        {
            return element.IsOffscreen;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Modal de cobro: elige Efectivo, captura el monto autocompletado, agrega el pago, finaliza y
    /// devuelve el N° de factura y el monto Bs.S cobrado que muestra la UI real.
    /// </summary>
    private async Task<CheckoutOutcome> CompleteCashCheckout(Window window, UIA3Automation dialogAutomation, TimeSpan findTimeout)
    {
        var checkoutButton = WaitForEnabledButton(window, "Pos_CheckoutButton", findTimeout)
            ?? throw new InvalidOperationException($"El botón COBRAR del POS no se habilitó con el carrito cargado. {DescribeUi(window)}");
        checkoutButton.Invoke();

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

        Assert.True(cashMethod.Result != null, $"No se pudo seleccionar el método 'Efectivo' en el modal de cobro. {DescribeUi(window)}");

        var amountFilled = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var text = window.FindFirstDescendant(cf => cf.ByAutomationId("Checkout_AmountInput"))?.AsTextBox()?.Text;
                return ParseUiAmount(text) > 0 ? text : null;
            }),
            findTimeout);

        Assert.True(amountFilled.Result != null, $"El monto Bs.S del cobro no se autocompletó al elegir 'Efectivo'. {DescribeUi(window)}");

        decimal paidBsS = ParseUiAmount(amountFilled.Result);

        var addPaymentButton = WaitForEnabledButton(window, "Checkout_AddPaymentButton", findTimeout)
            ?? throw new InvalidOperationException($"El botón de agregar pago del modal de cobro no apareció habilitado. {DescribeUi(window)}");
        addPaymentButton.Invoke();

        var finalizeButton = WaitForEnabledButton(window, "Checkout_FinalizeSaleButton", findTimeout)
            ?? throw new InvalidOperationException(
                $"El botón COBRAR Y FINALIZAR no se habilitó: el pago no quedó registrado. {ReadWarningDialog(window)} {DescribeUi(window)}");
        finalizeButton.Invoke();

        var invoiceStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var invoiceSightings = new List<string>();
        string? invoiceMessage = null;
        var invoiceDeadline = DateTime.UtcNow + InvoiceWaitBudget(findTimeout);
        while (invoiceMessage == null && DateTime.UtcNow < invoiceDeadline)
        {
            var topLevelElements = GetProcessTopLevelElements(dialogAutomation);
            RecordWindowSightings(invoiceSightings, topLevelElements);
            invoiceMessage = UiaRetry.RetryUia(() => FindInvoiceMessage(window, topLevelElements));
            if (invoiceMessage == null)
            {
                Thread.Sleep(200);
            }
        }

        invoiceStopwatch.Stop();

        int invoiceNumber;
        if (invoiceMessage != null)
        {
            var match = InvoiceNumberPattern.Match(invoiceMessage);
            invoiceNumber = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            // El diálogo de éxito es una ventana transitoria que en corridas cargadas puede no
            // exponerse a UIA aunque el servidor ya persistió la venta. Si no hay error visible, el
            // N° real se recupera del historial de caja del MISMO stack (el ingreso físico de la
            // venta quedó registrado con su factura) y las aserciones de caja posteriores confirman
            // el efecto real de la venta en la UI.
            string errorDetail = DescribeTopLevelDialog(dialogAutomation, window, "CustomDialogWindow_Error");
            invoiceNumber = await GetLatestSaleInvoiceFromDrawerAsync();
            Assert.True(
                invoiceNumber > 0,
                $"La UI no mostró el N° de factura tras finalizar el cobro (espera {invoiceStopwatch.Elapsed.TotalSeconds:0.0} s) " +
                $"y el historial de caja tampoco lo expone. {errorDetail} {FormatSightings(invoiceSightings)} {DescribeUi(window)}");
        }

        // Cierre por la acción primaria (no se pulsa "Guardar Recibo" para no abrir el PDF). Si el
        // diálogo se autocerró, el flujo continúa igual: no es un fallo.
        var continueButton = Retry.WhileNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByName("Continuar"))?.AsButton()),
            findTimeout);
        continueButton.Result?.Invoke();

        return new CheckoutOutcome(invoiceNumber, paidBsS);
    }

    /// <summary>
    /// Diálogo real de CASH IN (DialogHost del shell): monto entero y motivo, confirmación y espera
    /// de cierre del diálogo. La transacción y la recarga de la vista las dispara el ViewModel.
    /// </summary>
    private static void CompleteCashIn(Window window, TimeSpan findTimeout)
    {
        var amountInput = WaitForElement(window, "CashTransaction_AmountInput", findTimeout)?.AsTextBox()
            ?? throw new InvalidOperationException($"El diálogo de CASH IN no apareció (monto). {DescribeUi(window)}");
        var reasonInput = WaitForElement(window, "CashTransaction_ReasonInput", findTimeout)?.AsTextBox()
            ?? throw new InvalidOperationException($"El diálogo de CASH IN no apareció (motivo). {DescribeUi(window)}");

        amountInput.Text = CashInAmountBsS.ToString("0", CultureInfo.InvariantCulture);
        reasonInput.Text = "E2E T5 cash in";

        var confirmButton = WaitForElement(window, "CashTransaction_ConfirmButton", findTimeout)?.AsButton()
            ?? throw new InvalidOperationException($"El botón CONFIRM del CASH IN no apareció. {DescribeUi(window)}");
        confirmButton.Invoke();

        var closed = Retry.WhileNotNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId("CashTransaction_AmountInput"))),
            findTimeout);
        Assert.True(closed.Success, $"El diálogo de CASH IN no se cerró tras confirmar. {DescribeUi(window)}");
    }

    /// <summary>
    /// Confirmación del cierre: el confirm nativo debe mostrar el faltante calculado; tras el "Sí"
    /// aparece el info de éxito real (el cierre ya se persistió y la sesión rotó en el servidor).
    /// </summary>
    private void ConfirmAndDismissClosureDialogs(UIA3Automation dialogAutomation, Window window, TimeSpan findTimeout)
    {
        var confirmDeadline = DateTime.UtcNow + findTimeout;
        Window? confirmDialog = null;
        var confirmSightings = new List<string>();
        while (confirmDialog == null && DateTime.UtcNow < confirmDeadline)
        {
            var topLevelElements = GetProcessTopLevelElements(dialogAutomation);
            RecordWindowSightings(confirmSightings, topLevelElements);
            confirmDialog = FindDialogWindow(window, topLevelElements, "CustomDialogWindow_Confirm");
            if (confirmDialog == null)
            {
                Thread.Sleep(200);
            }
        }

        if (confirmDialog == null)
        {
            throw new InvalidOperationException(
                $"El confirm del cierre diario no apareció. {FormatSightings(confirmSightings)} {DescribeUi(window)}");
        }

        var confirmTexts = ReadAllTexts(confirmDialog);
        Assert.True(
            confirmTexts.Any(text => text.Contains("FALTANTE", StringComparison.OrdinalIgnoreCase)),
            $"El confirm del cierre no muestra el faltante del arqueo. Textos: [{string.Join(" | ", confirmTexts)}]");

        var yesButton = WaitForDialogButton(confirmDialog, "CustomDialog_ConfirmButton", "Sí", findTimeout)
            ?? throw new InvalidOperationException("El botón 'Sí' del confirm de cierre no apareció.");
        yesButton.Invoke();

        var infoBudget = ClosureDialogBudget(findTimeout);
        var infoDeadline = DateTime.UtcNow + infoBudget;
        Window? infoDialog = null;
        var infoSightings = new List<string>();
        bool uiReloaded = false;
        while (infoDialog == null && DateTime.UtcNow < infoDeadline)
        {
            var topLevelElements = GetProcessTopLevelElements(dialogAutomation);
            RecordWindowSightings(infoSightings, topLevelElements);
            infoDialog = FindDialogWindow(window, topLevelElements, "CustomDialogWindow_Info");
            if (infoDialog != null)
            {
                break;
            }

            uiReloaded = HasClosureReloadedInUi(window);
            if (uiReloaded)
            {
                // El info de éxito no es visible para UIA, pero el ViewModel ya recargó los totales
                // del nuevo turno (la recarga ocurre DESPUÉS de ShowInfo): el cierre quedó
                // confirmado por estado real. Gracia corta por si la ventana aparece demorada.
                infoDialog = WaitForDialogBriefly(window, dialogAutomation, "CustomDialogWindow_Info", TimeSpan.FromSeconds(2));
                break;
            }

            Thread.Sleep(200);
        }

        if (infoDialog == null)
        {
            if (uiReloaded)
            {
                return;
            }

            // El POST serializable del cierre incluye arqueo + rollover + comprobantes antes de
            // responder; si además el servidor lo rechazó, el diálogo visible es el de error.
            throw new InvalidOperationException(
                $"El diálogo de éxito del cierre no apareció tras {infoBudget.TotalSeconds:0} s. " +
                $"{FormatSightings(confirmSightings)} {FormatSightings(infoSightings)} " +
                $"{DescribeTopLevelDialog(dialogAutomation, window, "CustomDialogWindow_Error")} {DescribeUi(window)}");
        }

        var infoTexts = ReadAllTexts(infoDialog);
        Assert.True(
            infoTexts.Any(text => text.Contains("Cierre diario procesado", StringComparison.OrdinalIgnoreCase)),
            $"El éxito del cierre no se confirmó en la UI. Textos: [{string.Join(" | ", infoTexts)}]");

        var acceptButton = WaitForDialogButton(infoDialog, "CustomDialog_ConfirmButton", "Aceptar", findTimeout)
            ?? throw new InvalidOperationException("El botón 'Aceptar' del info de cierre no apareció.");
        acceptButton.Invoke();
    }

    // ─────────────────────────────── Verificación server-side (API real) ───────────────────────────

    /// <summary>
    /// N° de factura del ingreso físico de venta más reciente, leído del historial real de caja del
    /// harness. Es el fallback cuando el diálogo transitorio de éxito no se expone a UIA; la venta
    /// ya quedó persistida en el MISMO stack (el ingreso en efectivo guarda su factura).
    /// </summary>
    private async Task<int> GetLatestSaleInvoiceFromDrawerAsync()
    {
        try
        {
            using var api = await CreateAuthenticatedApiClientAsync();
            using var response = await api.GetAsync("api/cashdrawer/history?limit=10");
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("invoiceNumber", out var invoiceProperty) &&
                    invoiceProperty.ValueKind == JsonValueKind.Number &&
                    invoiceProperty.TryGetInt32(out int invoiceNumber) &&
                    invoiceNumber > 0)
                {
                    return invoiceNumber;
                }
            }
        }
        catch (Exception)
        {
            // Best-effort: sin factura recuperable, la aserción del caller falla con diagnóstico.
        }

        return 0;
    }

    /// <summary>
    /// Cliente HTTP autenticado contra el backend real del harness (login por API con la clave
    /// rotada). Solo LECTURAS de verificación: la operación del flujo la ejecuta la UI.
    /// </summary>
    private async Task<HttpClient> CreateAuthenticatedApiClientAsync()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(_fixture.BackendBaseAddress, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Add("X-Client-Version", ClientVersion);

        using var response = await client.PostAsJsonAsync(
            "api/auth/login",
            new { Cedula = _fixture.AdminUsername, Password = _fixture.AdminPassword });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            client.Dispose();
            throw new InvalidOperationException($"Login de verificación API falló con {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        using var json = JsonDocument.Parse(body);
        var token = json.RootElement.TryGetProperty("token", out var tokenProperty) ? tokenProperty.GetString() : null;
        if (string.IsNullOrWhiteSpace(token))
        {
            client.Dispose();
            throw new InvalidOperationException("El login de verificación API no devolvió token.");
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<CashSessionSnapshot?> GetActiveSessionAsync(HttpClient api)
    {
        using var response = await api.GetAsync("api/cashdrawer/active-session");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GET active-session falló con {(int)response.StatusCode}: {body}");
        }

        using var json = JsonDocument.Parse(body);
        if (json.RootElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return new CashSessionSnapshot(
            json.RootElement.GetProperty("id").GetInt32(),
            json.RootElement.GetProperty("openingBalanceLocal").GetDecimal());
    }

    private async Task<decimal> GetCurrentBalanceAsync(HttpClient api, int sessionId)
    {
        using var response = await api.GetAsync($"api/cashdrawer/current-balance?sessionId={sessionId}");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GET current-balance falló con {(int)response.StatusCode}: {body}");
        }

        return decimal.Parse(body, NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    private async Task<decimal> GetExpectedTotalsTotalAsync(HttpClient api)
    {
        var dateUtc = Uri.EscapeDataString(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        using var response = await api.GetAsync($"api/dailyclosure/expected-totals?dateUtc={dateUtc}");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GET expected-totals falló con {(int)response.StatusCode}: {body}");
        }

        using var json = JsonDocument.Parse(body);
        decimal total = 0m;
        foreach (var item in json.RootElement.EnumerateArray())
        {
            total += item.GetProperty("expectedAmountBsS").GetDecimal();
        }

        return total;
    }

    // ─────────────────────────────────────────── Helpers UIA ───────────────────────────────────────

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
    /// Botón de un diálogo ya localizado, con reintento acotado dentro del presupuesto: el peer del
    /// botón puede quedar transitoriamente fuera del árbol UIA (WindowStyle=None) aunque los textos
    /// del diálogo ya sean legibles. Prefiere el AutomationId estable y cae al Name visible.
    /// </summary>
    private static Button? WaitForDialogButton(AutomationElement dialog, string automationId, string fallbackName, TimeSpan timeout)
    {
        var result = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
                dialog.FindFirstDescendant(cf => cf.ByAutomationId(automationId))?.AsButton()
                ?? dialog.FindFirstDescendant(cf => cf.ByName(fallbackName))?.AsButton()),
            timeout);
        return result.Result;
    }

    /// <summary>
    /// Textos de un diálogo si está presente (diagnóstico de fallos): permite distinguir un cierre
    /// rechazado por el servidor de una simple demora del info de éxito.
    /// </summary>
    private string DescribeTopLevelDialog(UIA3Automation automation, Window mainWindow, string automationId)
    {
        try
        {
            var dialog = FindDialogWindow(mainWindow, GetProcessTopLevelElements(automation), automationId);
            if (dialog == null)
            {
                return $"Sin diálogo '{automationId}' visible.";
            }

            return $"Diálogo '{automationId}': [{string.Join(" | ", ReadAllTexts(dialog))}].";
        }
        catch (Exception ex)
        {
            return $"Lectura del diálogo '{automationId}' falló: {ex.Message}.";
        }
    }

    /// <summary>
    /// Busca un diálogo propio de la app por AutomationId: primero entre los hijos de nivel superior
    /// del proceso (sin filtro de ControlType, que puede excluir ventanas emergentes WPF) y como
    /// respaldo bajo el árbol de la principal (UIA expone las ventanas con Owner en la vista cruda
    /// de su dueña).
    /// </summary>
    private static Window? FindDialogWindow(Window mainWindow, IReadOnlyList<AutomationElement> topLevelElements, string automationId)
    {
        foreach (var element in topLevelElements)
        {
            if (string.Equals(element.Properties.AutomationId.ValueOrDefault, automationId, StringComparison.Ordinal))
            {
                return element.AsWindow();
            }

            try
            {
                var descendant = element.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                if (descendant != null)
                {
                    return descendant.AsWindow();
                }
            }
            catch (Exception)
            {
                // Elemento no disponible durante la búsqueda: se continúa con el siguiente.
            }
        }

        try
        {
            return mainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId))?.AsWindow();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private Window? WaitForDialogBriefly(Window mainWindow, UIA3Automation automation, string automationId, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            var dialog = FindDialogWindow(mainWindow, GetProcessTopLevelElements(automation), automationId);
            if (dialog != null)
            {
                return dialog;
            }

            Thread.Sleep(150);
        }

        return null;
    }

    /// <summary>
    /// true si el cierre ya impactó la UI: los totales esperados del nuevo turno quedaron en 0 y el
    /// estado muestra CUADRADO EXACTO (la recarga del ViewModel ocurre DESPUÉS de mostrar el info
    /// real de éxito). Al confirmar el arqueo, el esperado de la sesión previa era positivo, así que
    /// este par es una señal inequívoca del cierre procesado.
    /// </summary>
    private static bool HasClosureReloadedInUi(Window window)
    {
        try
        {
            decimal expected = ParseUiAmount(
                window.FindFirstDescendant(cf => cf.ByAutomationId("DailyClosure_ExpectedTotalText"))?.Name);
            string? status = window.FindFirstDescendant(cf => cf.ByAutomationId("DailyClosure_DifferenceStatusLabel"))?.Name;
            return expected == 0m
                && status != null
                && status.Contains("CUADRADO EXACTO", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string[] ReadAllTexts(Window window)
        => UiaRetry.RetryUia(() => window
            .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Select(element => element.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray()) ?? Array.Empty<string>();

    private static decimal ReadAmountText(Window window, string automationId)
    {
        var element = UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)))
            ?? throw new InvalidOperationException($"'{automationId}' no está presente en la UI. {DescribeUi(window)}");
        return ParseUiAmount(element.Name);
    }

    private static decimal WaitForPositiveAmountText(Window window, string automationId, TimeSpan timeout)
    {
        var result = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var parsed = ParseUiAmount(window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))?.Name);
                return parsed > 0 ? (decimal?)parsed : null;
            }),
            timeout);
        Assert.True(result.Result.HasValue, $"'{automationId}' no mostró un monto positivo. {DescribeUi(window)}");
        return result.Result!.Value;
    }

    private static decimal WaitForExactAmountText(Window window, string automationId, decimal expected, TimeSpan timeout)
    {
        var result = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var parsed = ParseUiAmount(window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))?.Name);
                return parsed == expected ? (decimal?)parsed : null;
            }),
            timeout);
        Assert.True(result.Result.HasValue, $"'{automationId}' no alcanzó el valor {expected}. {DescribeUi(window)}");
        return result.Result!.Value;
    }

    private static string WaitForStatusText(Window window, string automationId, string expectedToken, TimeSpan timeout)
    {
        var result = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var name = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))?.Name;
                return name != null && name.Contains(expectedToken, StringComparison.OrdinalIgnoreCase) ? name : null;
            }),
            timeout);
        Assert.True(result.Result != null, $"'{automationId}' no alcanzó el estado '{expectedToken}'. {DescribeUi(window)}");
        return result.Result!;
    }

    private static bool IsElementHidden(Window window, string automationId)
    {
        try
        {
            var element = UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)));
            return element == null || element.IsOffscreen;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void AssertNoErrorDialogs()
    {
        var dialogs = _fixture.FindErrorDialogs();
        Assert.True(
            dialogs.Length == 0,
            $"Diálogos de error inesperados durante el flujo de caja/cierre real: {string.Join(" | ", dialogs)}");
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

    private static string DescribeUi(Window window)
    {
        string ids;
        try
        {
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

        return $"Ids visibles: {ids}";
    }

    /// <summary>
    /// Parsea un monto de la UI con separadores tolerantes: la cultura del sistema/formato puede
    /// variar (es-VE "1.234,56" / en-US "1,234.56" / "474,63" / "474.63" / "474") y el signo es
    /// parte del monto cuando la UI muestra faltantes. El separador decimal es el último; un único
    /// separador con exactamente 3 dígitos detrás es de miles.
    /// </summary>
    private static decimal ParseUiAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0m;
        }

        // Arranca en un dígito (con signo opcional) para no capturar el punto de la etiqueta
        // "Bs.S" como separador.
        var match = Regex.Match(text, @"-?\d[\d.,]*");
        return match.Success ? ParseNormalizedNumber(match.Value) : 0m;
    }

    private static decimal ParseNormalizedNumber(string raw)
    {
        bool negative = raw.StartsWith('-');
        string digits = raw.TrimStart('-');

        int lastDot = digits.LastIndexOf('.');
        int lastComma = digits.LastIndexOf(',');

        string normalized;
        if (lastDot >= 0 && lastComma >= 0)
        {
            // Ambos separadores: el decimal es el que aparece al final; el otro es de miles.
            normalized = lastDot > lastComma
                ? digits.Replace(",", string.Empty)
                : digits.Replace(".", string.Empty).Replace(',', '.');
        }
        else if (lastComma >= 0)
        {
            normalized = digits.Count(c => c == ',') == 1 && digits.Length - lastComma - 1 == 3
                ? digits.Replace(",", string.Empty)
                : digits.Replace(',', '.');
        }
        else if (lastDot >= 0)
        {
            normalized = digits.Count(c => c == '.') == 1 && digits.Length - lastDot - 1 == 3
                ? digits.Replace(".", string.Empty)
                : digits;
        }
        else
        {
            normalized = digits;
        }

        if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return 0m;
        }

        return negative ? -value : value;
    }

    private readonly record struct CheckoutOutcome(int InvoiceNumber, decimal PaidBsS);

    private readonly record struct CashSessionSnapshot(int Id, decimal OpeningBalanceLocal);
}
