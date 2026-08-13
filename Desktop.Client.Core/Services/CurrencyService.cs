using System;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Messages;
using Microsoft.AspNetCore.SignalR.Client;

namespace Desktop.Client.Services;

public class CurrencyService : ICurrencyService
{
    private decimal _currentRate;
    private readonly HubConnection _hubConnection;

    public CurrencyService()
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl("http://localhost:5000/hubs/exchange-rate")
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<decimal>("ReceiveRateUpdate", (newRate) =>
        {
            // Execute on UI thread to ensure any observing UI elements don't crash from cross-thread access
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentRate = newRate;
            });
        });

        _ = StartConnectionAsync();
    }

    private async Task StartConnectionAsync()
    {
        try
        {
            await _hubConnection.StartAsync();
        }
        catch
        {
            // If the server is offline initially, a manual reconnect logic might be needed 
            // since WithAutomaticReconnect works after successful initial connection.
            // For now, we rely on the manual sync and normal restart if disconnected.
            // Also adding a crude retry wrapper for initial start
            await RetryConnectionLoopAsync();
        }
    }

    private async Task RetryConnectionLoopAsync()
    {
        while (_hubConnection.State == HubConnectionState.Disconnected)
        {
            try
            {
                await Task.Delay(5000);
                await _hubConnection.StartAsync();
            }
            catch { }
        }
    }

    public decimal CurrentRate
    {
        get => _currentRate;
        set
        {
            if (_currentRate != value)
            {
                _currentRate = value;
                WeakReferenceMessenger.Default.Send(new CurrencyRateChangedMessage(_currentRate));
            }
        }
    }
}
