using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Messages;
using Microsoft.AspNetCore.SignalR.Client;
using System.Linq;

namespace Desktop.Client.Services;

/// <summary>
/// Unified service for managing exchange rates. Handles API interaction, 
/// persistence, and real-time SignalR updates.
/// </summary>
public class ExchangeRateService : IExchangeRateService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly HubConnection _hubConnection;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private decimal _currentRate;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ExchangeRateService(HttpClient httpClient)
    {
        _httpClient = httpClient;

        var baseAddress = httpClient.BaseAddress ?? new Uri("http://localhost:5000/");
        var hubUri = new Uri(baseAddress, "hubs/exchange-rate");

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                options.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler clientHandler)
                    {
                        // For local network / self-signed certificate setups, avoid connection rejection
                        clientHandler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
                    }
                    return handler;
                };
            })
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<decimal>("ReceiveRateUpdate", async (newRate) =>
        {
            await UpdateRateLocallyAsync(newRate);
        });

        _hubConnection.On("OnHoldSalesUpdated", () =>
        {
            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    WeakReferenceMessenger.Default.Send(new OnHoldSalesRefreshMessage());
                });
            }
            else
            {
                WeakReferenceMessenger.Default.Send(new OnHoldSalesRefreshMessage());
            }
        });

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        // Initial sync from API — if this fails, the app starts with rate = 0
        // which is safe (PricingHelper returns 0 for rate <= 0)
        try
        {
            await GetCurrentRateAsync();
        }
        catch
        {
            // Rate stays at 0; user will see "0.00" and can set it manually
        }

        // Start SignalR in background — never blocks the UI
        _ = StartSignalRAsync();
    }

    public decimal CurrentRate
    {
        get => _currentRate;
        set => _ = UpdateRateLocallyAsync(value);
    }

    private async Task UpdateRateLocallyAsync(decimal newRate)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (_currentRate != newRate)
            {
                _currentRate = newRate;
                
                // Broadcast change to the rest of the application
                if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        WeakReferenceMessenger.Default.Send(new ExchangeRateChangedMessage(_currentRate));
                    });
                }
                else
                {
                    WeakReferenceMessenger.Default.Send(new ExchangeRateChangedMessage(_currentRate));
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task StartSignalRAsync()
    {
        const int _max_retries = 60; // ~5 minutes of retries
        int _attempt = 0;

        while (_attempt < _max_retries)
        {
            try
            {
                if (_hubConnection.State == HubConnectionState.Disconnected)
                {
                    await _hubConnection.StartAsync();
                }
                return; // Connected successfully
            }
            catch
            {
                _attempt++;
                await Task.Delay(5000);
            }
        }
    }

    public async Task<(decimal Rate, DateTime? LastUpdated)> GetCurrentRateAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("api/exchange-rate/today");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>(_jsonOptions);
            decimal rate = json?.Value ?? 0m;
            if (rate > 0)
            {
                await UpdateRateLocallyAsync(rate);
            }
            return (rate, json?.UpdatedAt);
        }
        catch
        {
            return (0m, null);
        }
    }

    public async Task SaveRateAsync(decimal rate)
    {
        var response = await _httpClient.PostAsJsonAsync("api/exchange-rate", new { Value = rate });
        response.EnsureSuccessStatusCode();
        await UpdateRateLocallyAsync(rate);
    }

    public async Task<List<ExchangeRateHistoryDto>> GetHistoryAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("api/exchange-rate/history");
            response.EnsureSuccessStatusCode();

            var history = await response.Content.ReadFromJsonAsync<List<ExchangeRateHistoryDto>>(_jsonOptions);
            return history ?? new List<ExchangeRateHistoryDto>();
        }
        catch
        {
            return new List<ExchangeRateHistoryDto>();
        }
    }

    public async Task<(decimal Rate, DateTime? LastUpdated)> SyncBcvAsync()
    {
        var response = await _httpClient.PostAsync("api/exchange-rate/sync-bcv", null);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>(_jsonOptions);
        decimal rate = json?.Value ?? 0m;
        if (rate > 0)
        {
            await UpdateRateLocallyAsync(rate);
        }
        return (rate, json?.UpdatedAt);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
        }
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private class ExchangeRateResponse
    {
        public decimal Value { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
