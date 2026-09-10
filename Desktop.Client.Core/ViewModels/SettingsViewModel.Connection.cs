using System;
using System.Windows;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

public partial class SettingsViewModel
{
    private void UpdateConnectionStatusDisplay(ConnectionStatus status)
    {
        switch (status)
        {
            case ConnectionStatus.Connected:
                ConnectionStatusText = "Conectado";
                ConnectionStatusColor = "#27AE60";
                break;
            case ConnectionStatus.Connecting:
                ConnectionStatusText = "Conectando...";
                ConnectionStatusColor = "#F39C12";
                break;
            case ConnectionStatus.Scanning:
                ConnectionStatusText = "Buscando servidor...";
                ConnectionStatusColor = "#F39C12";
                break;
            case ConnectionStatus.Disconnected:
            default:
                ConnectionStatusText = "Desconectado";
                ConnectionStatusColor = "#E74C3C";
                break;
        }
    }
}