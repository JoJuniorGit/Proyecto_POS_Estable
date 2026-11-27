using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Npgsql;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

/// <summary>
/// Caja chica real de punta a punta (Paso 13 / 8.188, T4): login Admin, turno de caja activo,
/// panel de Caja Chica, registro de un gasto Bs.S por el modal real (monto/categoría/concepto/
/// beneficiario/referencia), verificación del listado y del resumen del turno, anulación DIRECTA
/// de Admin con motivo, estado Anulado y descarga del vale contra el MISMO stack real
/// (Backend.API + PostgreSQL aislada + cliente SIN <c>--e2e</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>La UI no expone apertura de sesión de caja (el turno nace del cobro): el sustrato se crea
/// por la API real del harness (<c>POST /api/cashdrawer/open</c>) como setup, no como flujo bajo
/// prueba; la vista carga ese turno al navegar/refrescar.</item>
/// <item>La grilla del panel es un <c>PerformantDataGrid</c> que anula su AutomationPeer (fix de OOM
/// en DataGrid): las celdas y los botones de fila (Anular / Descargar vale) NO son alcanzables por
/// UIA. La fila SÍ se materializa como contenedor y el resumen del panel es texto accesible, así que
/// la UI se verifica por ambos; las acciones de fila se ejercen contra los MISMOS endpoints reales
/// que consumen esos botones (precedente del harness: los comprobantes también se verifican por
/// API para no abrir visores externos).</item>
/// <item>El success dialog del registro ofrece "Imprimir Vale" como acción secundaria: se pulsa
/// "Continuar" y NO se imprime, para no abrir el visor PDF del sistema (precedente
/// <c>FullStackCashClosureTests</c>).</item>
/// </list>
/// Gateado por <c>E2E_POSTGRES_CONNECTION</c>: local sin variable retorna en silencio; en CI sin
/// variable falla cerrado (mismo contrato que el resto del harness full-stack).
/// </remarks>
public class PettyCashExpenseTests : IClassFixture<FullStackFixture>
{
    private const decimal OpeningBalanceBsS = 5000m;
    private const decimal ExpenseAmountBsS = 150m;
    private const string ExpenseCategory = "Insumos y Limpieza";
    private const string ExpenseConcept = "E2E insumos de limpieza";
    private const string ExpenseBeneficiary = "Proveedor E2E";
    private const string ExpenseReference = "REF-E2E-001";
    private const string CancellationReason = "E2E anulación del gasto";
    private const int PettyCashStatusActive = 0;
    private const int PettyCashStatusCancelled = 1;

    /// <summary>Gasto que permanece Active para el renglón OPEX del cierre Z.</summary>
    private const decimal ActiveExpenseAmountBsS = 30m;

    private readonly FullStackFixture _fixture;

    public PettyCashExpenseTests(FullStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PettyCashExpense_RegisterAndCancelByAdmin_ReflectsInPanelAndServer()
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

            using var automation = new UIA3Automation();
            using var api = new E2eApiClient(_fixture.BackendBaseAddress);

            // 1) Login real con la clave rotada por el bootstrap + higiene de arranque.
            EnsureLoggedInWithBoundedRetry(window, findTimeout);
            DismissStaleRecoveryPromptIfPresent(window, findTimeout);
            AssertNoErrorDialogs("arranque");

            // 2) Sustrato del turno: la caja chica exige sesión de caja activa.
            var token = await LoginApiAsync(api);
            var sessionId = await api.OpenCashDrawerSessionAsync(token, OpeningBalanceBsS, bootstrap.ExchangeRate);

            // 3) Vista de caja + panel de caja chica (el refresh re-sincroniza el turno recién abierto).
            NavigateToView(window, "Nav_BtnCashDrawer", findTimeout);
            WaitForElement(window, "CashDrawer_RefreshButton", findTimeout);
            var pettyCashButton = WaitForPettyCashButton(window, findTimeout)
                ?? throw new InvalidOperationException(
                    $"El botón CAJA CHICA no se habilitó con la sesión #{sessionId} activa. {DescribeUi(window)}");
            pettyCashButton.Invoke();

            Assert.True(
                WaitForEnabledButton(window, "CashDrawer_PettyCashRegisterButton", findTimeout) != null,
                $"El panel de caja chica no expuso REGISTRAR GASTO. {DescribeUi(window)}");
            Assert.True(
                WaitForSummaryText(window, "0 activo(s)", findTimeout),
                $"El turno recién abierto no arrancó sin gastos activos. {DescribePettyCashGrid(window)}");
            // El turno aislado nace sin filas: si el listado ya materializara un DataItem, el conteo
            // posterior de la fila registrada sería un falso verde (mismo contenedor preexistente).
            Assert.True(
                WaitForPettyCashRowCount(window, 0, findTimeout),
                $"El listado de caja chica no arrancó vacío en el turno recién abierto. {DescribePettyCashGrid(window)}");

            // 4) Modal real de registro: Bs.S (default), monto, categoría, concepto, beneficiario y referencia.
            var registerButton = WaitForEnabledButton(window, "CashDrawer_PettyCashRegisterButton", findTimeout)!;
            registerButton.Invoke();

            var amountInput = WaitForProcessElement(automation, "PettyCashExpense_AmountInput", findTimeout)?.AsTextBox()
                ?? throw new InvalidOperationException("El modal de registro de caja chica no apareció (monto).");
            amountInput.Text = ExpenseAmountBsS.ToString("0", CultureInfo.InvariantCulture);
            SelectExpenseCategory(automation, ExpenseCategory, findTimeout);
            SetDialogText(automation, "PettyCashExpense_ConceptInput", ExpenseConcept, findTimeout);
            SetDialogText(automation, "PettyCashExpense_BeneficiaryInput", ExpenseBeneficiary, findTimeout);
            SetDialogText(automation, "PettyCashExpense_ReferenceInput", ExpenseReference, findTimeout);

            var confirmButton = WaitForEnabledProcessButton(automation, "PettyCashExpense_ConfirmButton", findTimeout)
                ?? throw new InvalidOperationException(
                    $"REGISTRAR GASTO no se habilitó con el formulario completo. {ReadExpenseDialogError(automation)}");
            confirmButton.Invoke();

            // 5) El modal cierra y el success dialog ofrece imprimir el vale: se despide sin abrir PDFs.
            WaitForProcessElementGone(automation, "PettyCashExpense_ConfirmButton", findTimeout);
            DismissVoucherOffer(automation, findTimeout);

            // 6) El listado del turno refleja el gasto: fila materializada + resumen contabilizado + servidor.
            Assert.True(
                WaitForPettyCashRowCount(window, 1, findTimeout),
                $"El listado de caja chica no materializó la fila del gasto registrado. {DescribePettyCashGrid(window)}");
            Assert.True(
                WaitForSummaryText(window, "1 activo(s)", findTimeout),
                $"El resumen del panel no contabilizó el gasto registrado. {DescribePettyCashGrid(window)}");
            var activeExpense = (await api.GetPettyCashExpensesAsync(token, sessionId))
                .SingleOrDefault(expense => expense.Concept == ExpenseConcept)
                ?? throw new InvalidOperationException(
                    $"El servidor no reporta el gasto '{ExpenseConcept}' en la sesión #{sessionId}.");
            Assert.Equal(PettyCashStatusActive, activeExpense.Status);
            Assert.Equal(ExpenseAmountBsS, activeExpense.AmountBsS);
            Assert.True(activeExpense.VoucherNumber > 0, "El gasto registrado no recibió correlativo de vale.");

            // 7) Anulación DIRECTA de Admin (sin autorización de otro supervisor) con motivo, contra el
            //    MISMO endpoint del botón de fila (la celda no es alcanzable por UIA, ver remarks).
            var cancelledExpense = await api.CancelPettyCashExpenseAsync(token, activeExpense.Id, CancellationReason);
            Assert.Equal(PettyCashStatusCancelled, cancelledExpense.Status);
            Assert.Equal(CancellationReason, cancelledExpense.CancellationReason);

            // 8) La UI refleja el estado anulado: al refrescar el panel el turno queda sin activos y la
            //    fila histórica del gasto permanece listada.
            var refreshButton = WaitForEnabledButton(window, "CashDrawer_PettyCashRefreshButton", findTimeout)
                ?? throw new InvalidOperationException($"El panel de caja chica no expuso el refresh. {DescribeUi(window)}");
            refreshButton.Invoke();
            Assert.True(
                WaitForSummaryText(window, "0 activo(s)", findTimeout),
                $"El resumen del panel no reflejó la anulación del gasto. {DescribePettyCashGrid(window)}");
            Assert.True(
                WaitForPettyCashRowCount(window, 1, findTimeout),
                $"La fila histórica del gasto anulado desapareció del listado. {DescribePettyCashGrid(window)}");

            // 9) Espejo server-side del estado final.
            var persisted = (await api.GetPettyCashExpensesAsync(token, sessionId))
                .SingleOrDefault(expense => expense.Id == activeExpense.Id)
                ?? throw new InvalidOperationException($"El servidor no reporta el gasto #{activeExpense.Id} tras la anulación.");
            Assert.Equal(PettyCashStatusCancelled, persisted.Status);
            Assert.Equal(CancellationReason, persisted.CancellationReason);

            // 10) Vale disponible: descarga real sin error por el MISMO endpoint del botón.
            var download = await api.DownloadPettyCashVoucherAsync(token, activeExpense.Id);
            Assert.True(download.Bytes.Length > 0, "El vale descargado llegó vacío.");
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(download.Bytes, 0, Math.Min(4, download.Bytes.Length)));
            Assert.Equal("application/pdf", download.ContentType);

            // 11) Segundo gasto que permanece Active: el cierre debe reportar SOLO este en OPEX.
            var activeExpense2 = await api.RegisterPettyCashExpenseAsync(
                token,
                ActiveExpenseAmountBsS,
                "Gasto activo E2E",
                "Beneficiario Activo E2E",
                "REF-ACTIVA-E2E");
            var activeExpensePersisted = (await api.GetPettyCashExpensesAsync(token, sessionId))
                .SingleOrDefault(expense => expense.Id == activeExpense2.Id)
                ?? throw new InvalidOperationException("El servidor no reporta el gasto activo del cierre.");
            Assert.Equal(PettyCashStatusActive, activeExpensePersisted.Status);

            refreshButton = WaitForEnabledButton(window, "CashDrawer_PettyCashRefreshButton", findTimeout)
                ?? throw new InvalidOperationException(
                    $"El panel no expuso el refresh tras el gasto activo. {DescribeUi(window)}");
            refreshButton.Invoke();
            Assert.True(
                WaitForSummaryText(window, "1 activo(s)", findTimeout),
                $"El panel no reflejó el gasto activo del cierre. {DescribePettyCashGrid(window)}");
            Assert.True(
                WaitForPettyCashRowCount(window, 2, findTimeout),
                $"El listado no conservó la fila anulada junto a la activa. {DescribePettyCashGrid(window)}");

            // 12) Cierre Z real: el renglón OPEX solo suma el gasto Active; el par Expense/Reversal
            //     del gasto anulado netea cero en gaveta.
            var closureDate = DateTime.UtcNow;
            var expectedTotals = await api.GetExpectedTotalsAsync(token, closureDate);
            var closure = await api.CreateDailyClosureAsync(token, closureDate, expectedTotals);
            Assert.True(closure.ClosureId > 0, "El cierre no devolvió un id persistido.");
            // El esperado de Cash solo descuenta el gasto Active: -30 (si contara el anulado sería -60).
            Assert.Equal(-ActiveExpenseAmountBsS, closure.CashSystemAmount);

            await using var database = new NpgsqlConnection(_fixture.DatabaseConnectionString);
            await database.OpenAsync();

            async Task<decimal> NetCashAsync(string concept)
            {
                // El egreso original no siempre materializa PettyCashExpenseId en la misma
                // transacción; el neteo fiable es por sesión + descripción + signo del Type
                // (Income=0, Expense=1).
                await using var command = new NpgsqlCommand(
                    "SELECT COALESCE(SUM(CASE WHEN \"Type\" = 0 THEN \"AmountLocal\" ELSE -\"AmountLocal\" END), 0) " +
                    "FROM \"CashTransactions\" WHERE \"SessionId\" = @session " +
                    "AND \"Source\" IN (9, 10) AND \"Description\" LIKE @pattern",
                    database);
                command.Parameters.AddWithValue("session", sessionId);
                command.Parameters.AddWithValue("pattern", $"%{concept}%");
                return (decimal)(await command.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException($"No se pudo leer el efecto de caja de '{concept}'."));
            }

            Assert.Equal(0m, await NetCashAsync(ExpenseConcept)); // anulado: egreso − reversa
            Assert.Equal(-ActiveExpenseAmountBsS, await NetCashAsync("Gasto activo E2E"));

            // Renglón OPEX del cierre: solo gastos Active de la sesión (el anulado no suma).
            await using (var opexCommand = new NpgsqlCommand(
                "SELECT COALESCE(SUM(\"AmountBsS\"), 0) FROM \"PettyCashExpenses\" " +
                "WHERE \"SessionId\" = @session AND \"Status\" = 0",
                database))
            {
                opexCommand.Parameters.AddWithValue("session", sessionId);
                var opex = (decimal)(await opexCommand.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException("No se pudo leer el renglón OPEX de la base aislada."));
                Assert.Equal(ActiveExpenseAmountBsS, opex);
            }

            AssertNoErrorDialogs("cierre del flujo de caja chica");
        }
        finally
        {
            await _fixture.TeardownAsync();
        }
    }

    // ───────────────────────────────────── Setup / verificación por API real ─────────────────────

    private async Task<string> LoginApiAsync(E2eApiClient api)
    {
        var login = await api.LoginAsync(_fixture.AdminUsername, _fixture.AdminPassword);
        return login.Token ?? throw new InvalidOperationException(
            $"El login de verificación API no devolvió token (requiresPasswordChange={login.RequiresPasswordChange}).");
    }

    // ───────────────────────────────────────── Login real por UI ─────────────────────────────────

    /// <summary>
    /// Login real con reintentos acotados (mismo criterio que FullStackCashClosure: una pérdida
    /// transitoria de foco deja la vista a medio exponer y el helper compartido falla aunque las
    /// credenciales sean correctas).
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
                    // Activación best-effort: el reintento puede resolver igual.
                }

                Thread.Sleep(500);
            }
        }
    }

    /// <summary>Espera una vista de login estable (botón habilitado ~1.5 s) antes de delegar en el helper.</summary>
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

    /// <summary>Rechaza el confirm de recuperación de venta residual, si existe (mismo criterio que el flujo de venta).</summary>
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

    // ───────────────────────────────────────── Navegación real ───────────────────────────────────

    /// <summary>
    /// Navega por la barra lateral. En modo real el DrawerHost arranca cerrado; si la navegación
    /// estándar falla se abre con el engranaje del POS y se reintenta dentro del presupuesto.
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
                    return null; // Transición del drawer: el presupuesto decide.
                }
            }),
            findTimeout);

        Assert.True(
            navigated.Result != null,
            $"No se pudo navegar a '{navAutomationId}' por la barra lateral. {DescribeUi(window)}");
    }

    // ─────────────────────────────────── Acciones del panel de caja chica ───────────────────────

    /// <summary>Espera el botón CAJA CHICA habilitado; refresca la vista si el turno aún no estaba sincronizado.</summary>
    private static Button? WaitForPettyCashButton(Window window, TimeSpan findTimeout)
    {
        var perAttempt = TimeSpan.FromSeconds(Math.Min(findTimeout.TotalSeconds, 5));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var button = WaitForEnabledButton(window, "CashDrawer_PettyCashButton", perAttempt);
            if (button != null)
            {
                return button;
            }

            var refresh = window.FindFirstDescendant(cf => cf.ByAutomationId("CashDrawer_RefreshButton"))?.AsButton();
            if (refresh is { IsEnabled: true })
            {
                try
                {
                    refresh.Invoke();
                }
                catch (FlaUI.Core.Exceptions.ElementNotEnabledException)
                {
                    // Transición de la vista: el siguiente intento decide.
                }
            }

            Thread.Sleep(400);
        }

        return null;
    }

    /// <summary>Selecciona la categoría por su etiqueta con re-despacho acotado (mismo patrón del modal de cobro).</summary>
    private void SelectExpenseCategory(UIA3Automation automation, string label, TimeSpan findTimeout)
    {
        var selected = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var combo = FindProcessElementOnce(automation, "PettyCashExpense_CategoryCombo")?.AsComboBox();
                if (combo == null)
                {
                    return null;
                }

                if (IsComboItemSelected(combo, label))
                {
                    return combo;
                }

                try
                {
                    combo.Select(label);
                }
                catch (Exception ex) when (ex is FlaUI.Core.Exceptions.ElementNotAvailableException
                                           or FlaUI.Core.Exceptions.NoClickablePointException
                                           or FlaUI.Core.Exceptions.ElementNotEnabledException)
                {
                    return null; // El template del modal todavía se materializa: el presupuesto reintenta.
                }

                return IsComboItemSelected(combo, label) ? combo : null;
            }),
            findTimeout);

        Assert.True(selected.Result != null, $"No se pudo seleccionar la categoría '{label}' en el modal de caja chica.");
    }

    private static bool IsComboItemSelected(ComboBox combo, string label)
    {
        try
        {
            return string.Equals(combo.SelectedItem?.Text, label, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// El registro exitoso muestra el success dialog con la acción secundaria "Imprimir Vale": se pulsa
    /// "Continuar" (primaria) para no abrir un visor PDF externo. Si UIA no lo expone, el diálogo se
    /// autocierra a los 7 s y el presupuesto acotado lo absorbe.
    /// </summary>
    private void DismissVoucherOffer(UIA3Automation automation, TimeSpan findTimeout)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(findTimeout.TotalSeconds, 12));
        while (DateTime.UtcNow < deadline)
        {
            var continueButton = FindProcessElementByNameOnce(automation, "Continuar")?.AsButton();
            if (continueButton is { IsEnabled: true })
            {
                continueButton.Invoke();
                return;
            }

            Thread.Sleep(200);
        }
    }

    // ───────────────────────────────────────── Utilidades UIA ────────────────────────────────────

    /// <summary>
    /// Elemento por AutomationId en cualquier raíz del proceso: la ventana principal (las ventanas
    /// transparentes con Owner se exponen aplanadas bajo ella) y los top-level del proceso (los
    /// modales con borde son ventanas propias).
    /// </summary>
    private AutomationElement? FindProcessElementOnce(UIA3Automation automation, string automationId)
        => FindInProcessRoots(automation, root => root.FindFirstDescendant(cf => cf.ByAutomationId(automationId)));

    private AutomationElement? FindProcessElementByNameOnce(UIA3Automation automation, string name)
        => FindInProcessRoots(automation, root => root.FindFirstDescendant(cf => cf.ByName(name)));

    private AutomationElement? FindInProcessRoots(UIA3Automation automation, Func<AutomationElement, AutomationElement?> find)
    {
        var roots = new List<AutomationElement>();
        if (_fixture.ClientWindow is { } mainWindow)
        {
            roots.Add(mainWindow);
        }

        if (_fixture.App is { } app)
        {
            try
            {
                roots.AddRange(automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(app.ProcessId)));
            }
            catch (Exception)
            {
                // Sin raíces adicionales: la principal alcanza para los modales aplanados.
            }
        }

        foreach (var root in roots)
        {
            var element = find(root);
            if (element != null)
            {
                return element;
            }
        }

        return null;
    }

    private AutomationElement? WaitForProcessElement(UIA3Automation automation, string automationId, TimeSpan timeout)
        => Retry.WhileNull(() => UiaRetry.RetryUia(() => FindProcessElementOnce(automation, automationId)), timeout).Result;

    private Button? WaitForEnabledProcessButton(UIA3Automation automation, string automationId, TimeSpan timeout)
    {
        var result = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var button = FindProcessElementOnce(automation, automationId)?.AsButton();
                return button is { IsEnabled: true } ? button : null;
            }),
            timeout);
        return result.Result;
    }

    private void SetDialogText(UIA3Automation automation, string automationId, string value, TimeSpan findTimeout)
    {
        var input = WaitForProcessElement(automation, automationId, findTimeout)?.AsTextBox()
            ?? throw new InvalidOperationException($"El campo '{automationId}' del modal de caja chica no apareció.");
        input.Text = value;
    }

    private void WaitForProcessElementGone(UIA3Automation automation, string automationId, TimeSpan timeout)
    {
        var gone = Retry.WhileNotNull(
            () => UiaRetry.RetryUia(() => FindProcessElementOnce(automation, automationId)),
            timeout);
        Assert.True(gone.Success, $"El elemento '{automationId}' del modal de caja chica no desapareció tras confirmar.");
    }

    private static AutomationElement? WaitForElement(Window window, string automationId, TimeSpan timeout)
        => Retry.WhileNull(() => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))), timeout).Result;

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
    /// Cantidad de filas materializadas del listado de caja chica. El PerformantDataGrid expone sus
    /// contenedores de fila como DataItems (sin celdas): el conteo verifica el listado del turno.
    /// </summary>
    private static bool WaitForPettyCashRowCount(Window window, int expected, TimeSpan timeout)
    {
        var result = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var grid = window.FindFirstDescendant(cf => cf.ByAutomationId("CashDrawer_PettyCashGrid"));
                if (grid == null)
                {
                    return null;
                }

                int count = grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length;
                return count == expected ? (bool?)true : null;
            }),
            timeout);
        return result.Result == true;
    }

    /// <summary>Espera un texto visible que contenga el fragmento (p. ej. el resumen "N activo(s)" del panel).</summary>
    private static bool WaitForSummaryText(Window window, string fragment, TimeSpan timeout)
    {
        var result = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                foreach (var text in window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
                {
                    var name = text.Name;
                    if (name != null && name.Contains(fragment, StringComparison.Ordinal))
                    {
                        return text;
                    }
                }

                return null;
            }),
            timeout);
        return result.Result != null;
    }

    private string ReadExpenseDialogError(UIA3Automation automation)
    {
        try
        {
            var error = FindProcessElementOnce(automation, "PettyCashExpense_ErrorText")?.Name;
            return string.IsNullOrWhiteSpace(error) ? "(sin texto de error en el modal)" : $"error del modal: '{error}'";
        }
        catch (Exception ex)
        {
            return $"(lectura del error del modal falló: {ex.Message})";
        }
    }

    private void AssertNoErrorDialogs(string stage)
    {
        var dialogs = _fixture.FindErrorDialogs();
        Assert.True(dialogs.Length == 0, $"Diálogos de error inesperados ({stage}): {string.Join(" | ", dialogs)}");
    }

    // ─────────────────────────────────────── Diagnóstico de fallos ───────────────────────────────

    private static string DescribeUi(Window window)
    {
        var ids = new[]
        {
            "CashDrawer_PettyCashButton",
            "CashDrawer_PettyCashRegisterButton",
            "CashDrawer_PettyCashGrid"
        };

        var parts = ids.Select(automationId =>
        {
            try
            {
                var element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                return element == null
                    ? $"'{automationId}': ausente"
                    : $"'{automationId}': IsEnabled={element.IsEnabled}";
            }
            catch (Exception ex)
            {
                return $"'{automationId}': lectura falló ({ex.Message})";
            }
        });

        return $"UI: {string.Join(", ", parts)}.";
    }

    /// <summary>Volcado acotado del subárbol de la grilla y de textos del panel para diagnóstico de fallos UIA.</summary>
    private static string DescribePettyCashGrid(Window window)
    {
        try
        {
            var grid = window.FindFirstDescendant(cf => cf.ByAutomationId("CashDrawer_PettyCashGrid"));
            if (grid == null)
            {
                return "'CashDrawer_PettyCashGrid' ausente del árbol UIA.";
            }

            var descendants = grid.FindAllDescendants();
            var lines = descendants.Take(30).Select(DescribeElementShort);
            var summaryCandidates = UiaRetry.RetryUia(() => window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Select(text => text.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name) && name.Contains("activo(s)", StringComparison.Ordinal))
                    .Take(5)
                    .ToArray())
                ?? Array.Empty<string>();

            return $"Grilla: {descendants.Length} descendientes [{string.Join(" | ", lines)}]. " +
                   $"Resúmenes: [{string.Join(" | ", summaryCandidates)}].";
        }
        catch (Exception ex)
        {
            return $"Lectura de la grilla falló: {ex.Message}.";
        }
    }

    private static string DescribeElementShort(AutomationElement element)
    {
        string type = "?";
        string id = "?";
        string name = "?";
        try { type = element.ControlType.ToString(); } catch { }
        try { id = element.Properties.AutomationId.ValueOrDefault ?? "-"; } catch { }
        try { name = element.Name ?? "-"; } catch { }
        return $"{type}/'{id}'/{name}";
    }
}
