using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using CommandCenter.Wpf.E2ETests.Fixtures;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

/// <summary>
/// Política de reintentos UIA (S4 / D7): solo fallas transitorias de transporte
/// (<see cref="COMException"/> y <see cref="Win32Exception"/> con ERROR_ACCESS_DENIED), acotada por
/// presupuesto de intentos, sin enmascarar el resultado lógico de la operación ni propagar de más.
/// Son tests puros (sin fixture ni app) y no están gated.
/// </summary>
public class UiaRetryTests
{
    // COR_E_TIMEOUT: HRESULT del timeout de UI Automation reportado en las corridas flaky.
    private const int TransientUiaTimeoutHResult = unchecked((int)0x80131505);

    // ERROR_ACCESS_DENIED: código nativo que devuelve SendInput cuando una carrera de foco/UIPI
    // deniega la inyección de entrada (teclado o mouse) en las corridas gated.
    private const int ErrorAccessDenied = 5;

    [Fact]
    public void RetryUia_TransientComFaultsThenSuccess_ReturnsSuccessAfterRetries()
    {
        var invocations = 0;

        var result = UiaRetry.RetryUia(
            () =>
            {
                invocations++;
                if (invocations < 3)
                {
                    throw CreateTransientFault();
                }

                return "recovered";
            },
            attempts: 4,
            backoff: TimeSpan.Zero);

        Assert.Equal(3, invocations);
        Assert.Equal("recovered", result);
    }

    [Fact]
    public void RetryUia_PersistentComFaults_ThrowsTheLastFaultAfterExactlyTheAttemptBudget()
    {
        var invocations = 0;
        COMException? lastFault = null;

        var thrown = Assert.Throws<COMException>(() =>
        {
            _ = UiaRetry.RetryUia<string?>(
                () =>
                {
                    invocations++;
                    var fault = CreateTransientFault();
                    lastFault = fault;
                    throw fault;
                },
                attempts: 4,
                backoff: TimeSpan.Zero);
        });

        Assert.Equal(4, invocations);
        Assert.Same(lastFault, thrown);
    }

    [Fact]
    public void RetryUia_TransientWin32AccessDeniedThenSuccess_ReturnsSuccessAfterRetries()
    {
        var invocations = 0;

        var result = UiaRetry.RetryUia(
            () =>
            {
                invocations++;
                if (invocations < 3)
                {
                    throw CreateTransientInputDenialFault();
                }

                return "recovered";
            },
            attempts: 4,
            backoff: TimeSpan.Zero);

        Assert.Equal(3, invocations);
        Assert.Equal("recovered", result);
    }

    [Fact]
    public void RetryUia_PersistentWin32AccessDenied_ThrowsTheLastFaultAfterExactlyTheAttemptBudget()
    {
        var invocations = 0;
        Win32Exception? lastFault = null;

        var thrown = Assert.Throws<Win32Exception>(() =>
        {
            _ = UiaRetry.RetryUia<string?>(
                () =>
                {
                    invocations++;
                    var fault = CreateTransientInputDenialFault();
                    lastFault = fault;
                    throw fault;
                },
                attempts: 4,
                backoff: TimeSpan.Zero);
        });

        Assert.Equal(4, invocations);
        Assert.Same(lastFault, thrown);
        Assert.Equal(ErrorAccessDenied, thrown.NativeErrorCode);
    }

    [Fact]
    public void RetryUia_Win32ExceptionWithDifferentErrorCode_PropagatesOnTheFirstAttemptWithoutRetry()
    {
        var invocations = 0;
        var fault = new Win32Exception(2, "Fallo de entrada no transitorio (simulado).");

        var thrown = Assert.Throws<Win32Exception>(() =>
        {
            _ = UiaRetry.RetryUia<string?>(
                () =>
                {
                    invocations++;
                    throw fault;
                },
                attempts: 4,
                backoff: TimeSpan.Zero);
        });

        Assert.Equal(1, invocations);
        Assert.Same(fault, thrown);
        Assert.Equal(2, thrown.NativeErrorCode);
    }

    [Fact]
    public void RetryUia_NonComException_PropagatesOnTheFirstAttemptWithoutRetry()
    {
        var invocations = 0;

        var thrown = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = UiaRetry.RetryUia<string?>(
                () =>
                {
                    invocations++;
                    throw new InvalidOperationException("fallo lógico, no de transporte");
                },
                attempts: 4,
                backoff: TimeSpan.Zero);
        });

        Assert.Equal(1, invocations);
        Assert.Equal("fallo lógico, no de transporte", thrown.Message);
    }

    [Fact]
    public void RetryUia_LogicalNullResult_IsReturnedWithoutRetrying()
    {
        var invocations = 0;

        var result = UiaRetry.RetryUia<string?>(
            () =>
            {
                invocations++;
                return null;
            },
            attempts: 4,
            backoff: TimeSpan.Zero);

        Assert.Null(result);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public void RetryUia_VoidOverload_RetriesTransientComFaults()
    {
        var invocations = 0;

        UiaRetry.RetryUia(
            () =>
            {
                invocations++;
                if (invocations < 2)
                {
                    throw CreateTransientFault();
                }
            },
            attempts: 4,
            backoff: TimeSpan.Zero);

        Assert.Equal(2, invocations);
    }

    [Fact]
    public void RetryUia_AttemptBudgetBelowOne_IsRejectedAsInvalid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = UiaRetry.RetryUia(() => "value", attempts: 0, backoff: TimeSpan.Zero);
        });
    }

    [Theory]
    [InlineData("15", 15.0)]
    [InlineData("12.5", 12.5)]
    [InlineData(" 7 ", 7.0)]
    public void ParseFindTimeout_ValidValue_HonorsConfiguredSeconds(string raw, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), UiaRetry.ParseFindTimeout(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-es-un-numero")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    // Desborde de TimeSpan (1e300 s >> TimeSpan.MaxValue): finito pero inválido como duración.
    [InlineData("1e300")]
    public void ParseFindTimeout_AbsentOrInvalidValue_FallsBackToDocumentedDefault(string? raw)
    {
        Assert.Equal(UiaRetry.DefaultFindTimeout, UiaRetry.ParseFindTimeout(raw));
    }

    [Fact]
    public void FindTimeout_WithEnvironmentVariableSet_HonorsConfiguredValue()
    {
        var original = Environment.GetEnvironmentVariable(UiaRetry.FindTimeoutEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(UiaRetry.FindTimeoutEnvironmentVariable, "25");

            Assert.Equal(TimeSpan.FromSeconds(25), UiaRetry.FindTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UiaRetry.FindTimeoutEnvironmentVariable, original);
        }
    }

    [Fact]
    public void FindTimeout_WithEnvironmentVariableAbsent_UsesDocumentedDefault()
    {
        var original = Environment.GetEnvironmentVariable(UiaRetry.FindTimeoutEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(UiaRetry.FindTimeoutEnvironmentVariable, null);

            Assert.Equal(UiaRetry.DefaultFindTimeout, UiaRetry.FindTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UiaRetry.FindTimeoutEnvironmentVariable, original);
        }
    }

    private static COMException CreateTransientFault()
    {
        return new COMException("Timeout transitorio de UI Automation (simulado).", TransientUiaTimeoutHResult);
    }

    private static Win32Exception CreateTransientInputDenialFault()
    {
        return new Win32Exception(ErrorAccessDenied, "Acceso denegado simulado de SendInput.");
    }
}
