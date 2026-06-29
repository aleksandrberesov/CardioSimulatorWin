using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using CardioSimulator.App.Data;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Theming;
using HelixToolkit.Geometry;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Assimp;
using HelixToolkit.SharpDX.Model.Scene;
using HelixToolkit.WinUI.SharpDX;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinColor = Windows.UI.Color;
using WinColors = Microsoft.UI.Colors;

namespace CardioSimulator.App.Controls;

/// <summary>
/// "3D" heart window opened from the monitor control panel. A modal pop-over
/// (<see cref="ContentDialog"/>, so it floats above the native Win2D monitor surface). Lays out the
/// three panels from the design: a left column of function buttons, a middle description panel
/// ("what is happening / a 12-lead ECG window"), and an interactive 3D heart viewport with an
/// "ECG lead" button on the right. The model is chosen in Settings (see <see cref="HeartModelStore"/>).
///
/// The viewport is a HelixToolkit.WinUI.SharpDX <see cref="Viewport3DX"/> (DirectX 11 via a
/// <c>SwapChainPanel</c>). Orbit / zoom / pan come from the built-in camera controller
/// (left-drag = orbit, right-drag = pan, wheel = zoom). It loads FBX/OBJ/glTF/etc. through
/// <see cref="Importer"/> (SharpAssimp); the model to load is resolved by <see cref="HeartModelStore"/>
/// (user override from Settings, else the bundled <c>Assets/Models/heart.*</c>), with a lit
/// placeholder sphere shown until a model is loaded.
/// </summary>
public sealed class Heart3DDialog
{
    private static readonly SolidColorBrush Cream = Brush(0xF2, 0xEF, 0xE6);
    private static readonly SolidColorBrush Blue = Brush(0x5B, 0x9B, 0xD5);
    private static readonly SolidColorBrush BlueHover = Brush(0x4F, 0x8B, 0xC2);
    private static readonly SolidColorBrush BluePressed = Brush(0x42, 0x7A, 0xAE);
    private static readonly SolidColorBrush White = Brush(0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush ErrorRed = Brush(0xC0, 0x39, 0x2B);
    private static readonly SolidColorBrush InfoGray = Brush(0x55, 0x55, 0x55);

    private Viewport3DX _viewport = null!;
    private SceneNodeGroupModel3D _modelRoot = null!;
    private DirectionalLight3D _headlight = null!;
    private MeshGeometryModel3D _placeholder = null!;
    private TextBlock _status = null!;
    private bool _busy;

    // xamlRoot is unused: the view mounts into the app's own Root grid (see ShowCoreAsync), but the
    // signature is kept so the call site (and the other monitor dialogs) stay uniform.
    public static Task ShowAsync(XamlRoot xamlRoot) => new Heart3DDialog().ShowCoreAsync();

    /// <summary>
    /// Shows the 3D view as a full-window overlay inside the app's own visual tree (the <c>Root</c>
    /// grid) — NOT a <see cref="ContentDialog"/> (a <c>SwapChainPanel</c> stays black in the popup
    /// layer) and NOT a separate <see cref="Window"/> (it must stay in-app). The shell behind is
    /// collapsed while the overlay is up: the monitor's Win2D surface renders above XAML siblings, so
    /// an opaque overlay with the shell hidden is the reliable approach (the same pattern
    /// <see cref="WelcomeOverlay"/> uses). Collapsing does not fire <c>Unloaded</c>, so the monitor
    /// canvas is not torn down.
    /// </summary>
    private Task ShowCoreAsync()
    {
        if (App.MainWindow?.Content is not Panel root)
        {
            return Task.CompletedTask;
        }

        // Hide the visible shell behind the overlay; remember what we hid so we can restore it on close.
        var hidden = new List<UIElement>();
        foreach (var child in root.Children)
        {
            if (child.Visibility == Visibility.Visible)
            {
                child.Visibility = Visibility.Collapsed;
                hidden.Add(child);
            }
        }

        Grid overlay = null!;
        void Close()
        {
            (_viewport.EffectsManager as IDisposable)?.Dispose();
            root.Children.Remove(overlay);
            foreach (var child in hidden)
            {
                child.Visibility = Visibility.Visible;
            }
        }

        overlay = BuildOverlay(Close);
        root.Children.Add(overlay); // added last ⇒ on top

        // Load the active model (user override or bundled default); otherwise the placeholder stays.
        TryAutoLoadModel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Full-bleed backdrop hosting the centered heart card; tapping the backdrop closes it. The shell
    /// is collapsed behind this overlay (so the monitor's Win2D surface can't bleed over it), so the
    /// backdrop is painted with the app's own page background — it reads as an in-app page rather than
    /// a black void around the card.
    /// </summary>
    private Grid BuildOverlay(Action onClose)
    {
        var overlay = new Grid { Background = AppTheme.PageBackground };
        var card = BuildCard(onClose);
        // Fill most of the window (leaving a backdrop margin), capped so it isn't huge on big monitors.
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        card.VerticalAlignment = VerticalAlignment.Stretch;
        card.Margin = new Thickness(40);
        card.MaxWidth = 1500;
        card.MaxHeight = 1000;
        overlay.Children.Add(card);
        overlay.Tapped += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, overlay))
            {
                onClose();
            }
        };
        return overlay;
    }

    /// <summary>The cream heart card: a title/close header above the three-panel content.</summary>
    private FrameworkElement BuildCard(Action onClose)
    {
        var header = new Grid { Padding = new Thickness(18, 10, 10, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = AppStrings.Monitor3DTitle,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        header.Children.Add(title);
        var close = new Button
        {
            Content = new SymbolIcon(Symbol.Cancel),
            Background = new SolidColorBrush(WinColors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, _) => onClose();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        // Header pinned at the top (Auto), content fills the remaining card height (Star) so the
        // viewport inside can grow with the window.
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        body.Children.Add(header);
        var content = BuildContent();
        Grid.SetRow(content, 1);
        body.Children.Add(content);

        return new Border
        {
            Background = Cream,
            CornerRadius = new CornerRadius(12),
            BorderBrush = Brush(0xD2, 0xD5, 0xE3),
            BorderThickness = new Thickness(1),
            Child = body,
        };
    }

    private FrameworkElement BuildContent()
    {
        var grid = new Grid
        {
            Background = Cream,
            Padding = new Thickness(16),
            ColumnSpacing = 16,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // left: function buttons
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // middle: description panel
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // right: viewport fills the rest

        // Left column: function buttons.
        var left = new StackPanel { Spacing = 10, Width = 190, VerticalAlignment = VerticalAlignment.Top };
        left.Children.Add(FunctionButton(AppStrings.Monitor3DLeadScheme));
        left.Children.Add(FunctionButton(AppStrings.Monitor3DFunctionFormat(2)));
        left.Children.Add(FunctionButton(AppStrings.Monitor3DFunctionFormat(3)));
        left.Children.Add(FunctionButton(AppStrings.Monitor3DMi));
        left.Children.Add(FunctionButton(AppStrings.Monitor3DFunctionFormat(5)));
        left.Children.Add(FunctionButton(AppStrings.Monitor3DFunctionFormat(6)));
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // Middle column: description / 12-lead ECG panel.
        var middleText = new StackPanel { Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        middleText.Children.Add(PanelText(AppStrings.Monitor3DDescription));
        middleText.Children.Add(PanelText(AppStrings.Monitor3DOrEcg));
        var middle = new Border
        {
            Background = Blue,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20),
            Width = 280,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = middleText,
        };
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);

        // Right column: the 3D viewport fills the available space (Star row), with the status line and
        // ECG-lead button stacked beneath it.
        var right = new Grid { RowSpacing = 12 };
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var viewportFrame = new Border
        {
            Background = White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = BuildHeartViewport(),
        };
        Grid.SetRow(viewportFrame, 0);
        right.Children.Add(viewportFrame);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = ErrorRed,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(_status, 1);
        right.Children.Add(_status);

        var ecgButton = FunctionButton(AppStrings.Monitor3DEcgLead);
        Grid.SetRow(ecgButton, 2);
        right.Children.Add(ecgButton);

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return grid;
    }

    /// <summary>
    /// Builds the DirectX 11 heart viewport: an (initially empty) model root, a lit placeholder
    /// primitive, and an orbit/zoom/pan camera. Imported models are added to <see cref="_modelRoot"/>.
    /// </summary>
    private Viewport3DX BuildHeartViewport()
    {
        _viewport = new Viewport3DX
        {
            MinWidth = 320,
            MinHeight = 320,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            EffectsManager = new DefaultEffectsManager(),
            BackgroundColor = WinColors.White,
            // Inspect mode = orbit the camera around the model; pan/zoom enabled too.
            CameraMode = CameraMode.Inspect,
            IsRotationEnabled = true,
            IsPanEnabled = true,
            IsInertiaEnabled = true,
            Camera = new PerspectiveCamera
            {
                Position = new Vector3(0, 0, 9),
                LookDirection = new Vector3(0, 0, -9),
                UpDirection = new Vector3(0, 1, 0),
                FieldOfView = 45,
                NearPlaneDistance = 0.1,
                FarPlaneDistance = 1000,
            },
        };

        // Lighting: a strong ambient fill (so a surface is never fully black even when a directional
        // misses it) + a headlight aimed along the camera (re-aimed at the model after framing) + a
        // back fill. The high ambient is deliberate — it rules lighting out as a cause of a black model.
        _viewport.Items.Add(new AmbientLight3D { Color = Rgb(120, 120, 120) });
        _headlight = new DirectionalLight3D { Color = WinColors.White, Direction = new Vector3(-0.3f, -0.5f, -1) };
        _viewport.Items.Add(_headlight);
        _viewport.Items.Add(new DirectionalLight3D { Color = Rgb(120, 120, 120), Direction = new Vector3(0.5f, 0.5f, 1) });

        // Container that imported model scene-nodes are added to.
        _modelRoot = new SceneNodeGroupModel3D();
        _viewport.Items.Add(_modelRoot);

        // Placeholder primitive (heart stand-in), shown until a real model loads.
        var builder = new MeshBuilder(true, true, false);
        builder.AddSphere(new Vector3(0, 0, 0), 2.2f, 48, 48);
        _placeholder = new MeshGeometryModel3D
        {
            Geometry = builder.ToMeshGeometry3D(),
            Material = PhongMaterials.Red,
        };
        _viewport.Items.Add(_placeholder);

        return _viewport;
    }

    /// <summary>Loads the active model (user override or bundled default); none ⇒ placeholder stays.</summary>
    private void TryAutoLoadModel()
    {
        var path = HeartModelStore.ResolveActiveModelPath();
        if (path is not null)
        {
            _ = LoadModelAsync(path);
        }
    }

    /// <summary>
    /// Imports a model off the UI thread (SharpAssimp), then swaps out the placeholder and frames
    /// the camera on the model. Failures leave the current scene untouched and show an inline message.
    /// </summary>
    private async Task LoadModelAsync(string path)
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        SetMessage(null, isError: false);

        try
        {
            var effects = _viewport.EffectsManager;
            var imported = await Task.Run<ImportedModel?>(() =>
            {
                var scene = new Importer().Load(path);
                var root = scene?.Root;
                if (root is null)
                {
                    return null;
                }
                // Pre-attach and lay out off the UI thread, then compute framing info.
                root.Attach(effects);
                root.UpdateAllTransformMatrix();
                root.TryGetBound(out var bound);
                root.TryGetCentroid(out var centroid);
                var maxDim = Math.Max(Math.Max(bound.Width, bound.Height), bound.Depth);
                return new ImportedModel(root, centroid, maxDim);
            });

            if (imported is null)
            {
                SetMessage(AppStrings.Monitor3DLoadFailed, isError: true);
                Log($"FAILED (no scene root): {path}");
                return;
            }

            _modelRoot.Clear();
            _modelRoot.AddNode(imported.Root);
            _placeholder.IsRendering = false;
            FrameCamera(imported.Centroid, imported.MaxDim);
        }
        catch (Exception ex)
        {
            SetMessage($"{AppStrings.Monitor3DLoadFailed}: {ex.Message}", isError: true);
            Log($"EXCEPTION: {path}\n{ex}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Positions the camera to frame a model of the given centroid/extent and orbits around it.</summary>
    private void FrameCamera(Vector3 centroid, float maxDim)
    {
        if (maxDim <= 0)
        {
            maxDim = 1f;
        }
        // Pull back enough to fit the model for the 45° vertical FOV, with margin.
        var distance = maxDim * 1.6f;
        var position = centroid + new Vector3(0, 0, distance);
        if (_viewport.Camera is PerspectiveCamera camera)
        {
            camera.Position = position;
            camera.LookDirection = centroid - position;
            camera.UpDirection = new Vector3(0, 1, 0);
            // Scale the clip planes to the model so a very large or very small FBX isn't clipped away.
            camera.NearPlaneDistance = Math.Max(0.01, distance * 0.01);
            camera.FarPlaneDistance = (distance + maxDim) * 4;
            _headlight.Direction = Vector3.Normalize(camera.LookDirection);
        }
        _viewport.FixedRotationPoint = centroid;
        _viewport.FixedRotationPointEnabled = true;
    }

    private void SetMessage(string? message, bool isError)
    {
        _status.Text = message ?? string.Empty;
        _status.Foreground = isError ? ErrorRed : InfoGray;
        _status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Appends a line to <c>%LOCALAPPDATA%\CardioSimulator\heart3d.log</c>; best-effort.</summary>
    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.AppendAllText(Path.Combine(AppPaths.Root, "heart3d.log"), $"{DateTimeOffset.Now:o} {line}\n");
        }
        catch
        {
            // diagnostics only — never throw
        }
    }

    /// <summary>A blue rounded button matching the design; flat color across all visual states.</summary>
    private static Button FunctionButton(string text)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
        };
        // Override the themed accent/hover brushes so the button stays the design blue throughout.
        button.Resources["ButtonBackground"] = Blue;
        button.Resources["ButtonBackgroundPointerOver"] = BlueHover;
        button.Resources["ButtonBackgroundPressed"] = BluePressed;
        button.Resources["ButtonForeground"] = White;
        button.Resources["ButtonForegroundPointerOver"] = White;
        button.Resources["ButtonForegroundPressed"] = White;
        return button;
    }

    private static TextBlock PanelText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Foreground = White,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Rgb(r, g, b));

    private static WinColor Rgb(byte r, byte g, byte b) => new() { A = 255, R = r, G = g, B = b };

    /// <summary>Result of an off-thread import: the attached scene root plus camera-framing info.</summary>
    private sealed record ImportedModel(SceneNode Root, Vector3 Centroid, float MaxDim);
}
