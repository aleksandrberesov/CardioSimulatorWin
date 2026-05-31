using System.ComponentModel;
using CardioSimulator.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CardioSimulator.App.Screens;

public sealed class CourseConstructorScreen : UserControl
{
    private readonly CourseConstructorViewModel _vm;
    
    private readonly ListView _courseList = new();
    private readonly ListView _lectureList = new();
    private readonly TextBox _markdownEditor = new();
    private readonly RichTextBlock _preview = new();

    public CourseConstructorScreen(CourseConstructorViewModel vm)
    {
        _vm = vm;
        
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        var navPanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 8, Margin = new Thickness(8) };
        navPanel.Children.Add(new TextBlock { Text = "Courses", FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        navPanel.Children.Add(_courseList);
        navPanel.Children.Add(new TextBlock { Text = "Lectures", FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        navPanel.Children.Add(_lectureList);
        Grid.SetColumn(navPanel, 0);

        _markdownEditor.AcceptsReturn = true;
        _markdownEditor.TextWrapping = TextWrapping.Wrap;
        _markdownEditor.Margin = new Thickness(8);
        Grid.SetColumn(_markdownEditor, 1);

        var previewPanel = new ScrollViewer { Content = _preview, Margin = new Thickness(8) };
        Grid.SetColumn(previewPanel, 2);

        grid.Children.Add(navPanel);
        grid.Children.Add(_markdownEditor);
        grid.Children.Add(previewPanel);

        Content = grid;
        
        _vm.PropertyChanged += OnVmChanged;
        _markdownEditor.TextChanged += (_, _) => _vm.SetMarkdown(_markdownEditor.Text);
        
        // Simple bindings for MVP
        UpdateLists();
    }

    private void UpdateLists()
    {
        // _courseList.ItemsSource = _vm.Repository.Courses; 
        // In a full implementation we would bind to the repository manifest directly
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CourseConstructorViewModel.TargetLecture))
        {
            _markdownEditor.Text = _vm.TargetLecture?.RawMarkdown ?? "";
        }
    }
}
