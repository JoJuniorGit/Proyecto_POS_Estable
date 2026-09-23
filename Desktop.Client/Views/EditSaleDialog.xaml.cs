using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Core.DTOs;
using Desktop.Client.ViewModels;
using Desktop.Client.Services;

namespace Desktop.Client.Views;

public partial class EditSaleDialog : Window
{
    private readonly EditSaleDialogViewModel _viewModel = new();

    public bool HasChanges => _viewModel.HasChanges;
    public IEnumerable<UpdateSaleItemDto>? ModifiedItems => _viewModel.ModifiedItems;

    public EditSaleDialog()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.CloseRequested += OnViewModelCloseRequested;
        Closed += OnWindowClosed;
    }

    public void LoadSale(SaleDto sale, decimal exchangeRate, IProductService? productService = null)
    {
        _viewModel.LoadSale(sale, exchangeRate, productService);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _viewModel.CloseRequested -= OnViewModelCloseRequested;
        _viewModel.Dispose();
    }

    private void LstSuggestions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.SelectedSuggestion != null && _viewModel.SelectSuggestionCommand.CanExecute(null))
        {
            _viewModel.SelectSuggestionCommand.Execute(null);
        }
    }

    private void OnViewModelCloseRequested(object? sender, EventArgs e)
    {
        DialogResult = _viewModel.HasChanges;
        Close();
    }
}