using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
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

    private Canvas _hotspotCanvas = null!;
    private Grid _hotspotDetailsPanel = null!;
    private TextBlock _hotspotDetailsTitle = null!;
    private TextBlock _hotspotDetailsDesc = null!;
    private Button _authoringModeButton = null!;
    private bool _authoringMode;
    private List<Hotspot> _hotspots = new();
    private string? _currentModelPath;
    private CameraAnimator? _activeAnimator;
    private Vector3 _lastCameraPos;
    private Vector3 _lastCameraLook;
    private Vector3 _lastCameraUp;
    private Vector2? _pressedPoint;
    private long _pressedTime;
    private Grid? _promptOverlay;

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
    ///
    /// Building the heart card spins up a DirectX 11 device (<see cref="DefaultEffectsManager"/>) on
    /// the UI thread, which can stall for a noticeable beat. So the overlay shows a spinner first, lets
    /// it paint (a frame ⇒ the compositor animates it off-thread), and only then builds the card — the
    /// click feels responsive instead of freezing until the viewport is ready.
    /// </summary>
    private async Task ShowCoreAsync()
    {
        if (App.MainWindow?.Content is not Panel root)
        {
            return;
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

        // Full-bleed backdrop; tapping it closes the overlay. The shell is collapsed behind it (so the
        // monitor's Win2D surface can't bleed over), so it's painted with the app's own page background.
        var overlay = new Grid { Background = AppTheme.PageBackground };
        void Close()
        {
            CancelCameraAnimation();
            StopCompositionRendering();
            // _viewport is null if the user closed during the loading spinner, before the card was built.
            (_viewport?.EffectsManager as IDisposable)?.Dispose();
            root.Children.Remove(overlay);
            foreach (var child in hidden)
            {
                child.Visibility = Visibility.Visible;
            }
        }
        overlay.Tapped += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, overlay))
            {
                Close();
            }
        };

        var loading = BuildLoadingIndicator();
        overlay.Children.Add(loading);
        root.Children.Add(overlay); // added last ⇒ on top

        // Let the spinner paint (and hand off to the compositor) before the synchronous viewport /
        // DirectX construction blocks the UI thread.
        await WaitForNextFrameAsync();

        // The user may have tapped the backdrop to dismiss while the spinner was up; if so the overlay
        // is gone — don't build (and leak) the DirectX viewport.
        if (overlay.Parent is null)
        {
            return;
        }

        var card = BuildCard(Close);
        // Fill most of the window (leaving a backdrop margin), capped so it isn't huge on big monitors.
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        card.VerticalAlignment = VerticalAlignment.Stretch;
        card.Margin = new Thickness(40);
        card.MaxWidth = 1500;
        card.MaxHeight = 1000;
        overlay.Children.Add(card);
        overlay.Children.Remove(loading);

        // Load the active model (user override or bundled default); otherwise the placeholder stays.
        TryAutoLoadModel();
        StartCompositionRendering();
    }

    /// <summary>Centered spinner + caption shown while the heavy 3D viewport is being constructed.</summary>
    private static FrameworkElement BuildLoadingIndicator()
    {
        var stack = new StackPanel
        {
            Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(new ProgressRing { IsActive = true, Width = 56, Height = 56 });
        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.Monitor3DLoading,
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return stack;
    }

    /// <summary>
    /// Completes after the next composition frame, i.e. once XAML has had a chance to paint the
    /// currently-mounted visuals. The spinner animates on the compositor (render) thread, so a single
    /// presented frame is enough for it to keep spinning even while the UI thread is later blocked
    /// building the DirectX viewport.
    /// </summary>
    private static Task WaitForNextFrameAsync()
    {
        var tcs = new TaskCompletionSource();
        void OnRendering(object? sender, object e)
        {
            CompositionTarget.Rendering -= OnRendering;
            tcs.TrySetResult();
        }
        CompositionTarget.Rendering += OnRendering;
        return tcs.Task;
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

        var viewportGrid = new Grid();
        var viewport = BuildHeartViewport();
        viewportGrid.Children.Add(viewport);

        _hotspotCanvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        viewportGrid.Children.Add(_hotspotCanvas);

        var toolbar = BuildHotspotsToolbar();
        viewportGrid.Children.Add(toolbar);

        var details = BuildHotspotDetailsPanel();
        viewportGrid.Children.Add(details);

        viewport.PointerPressed += Viewport_PointerPressed;
        viewport.PointerReleased += Viewport_PointerReleased;

        var viewportFrame = new Border
        {
            Background = White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = viewportGrid,
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
            LoadHotspots(path);
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

    private static string GetString(string en, string ru)
    {
        return AppStrings.Current == CardioSimulator.Core.Domain.Language.RU ? ru : en;
    }

    private void StartCompositionRendering()
    {
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnCompositionRendering;
    }

    private void StopCompositionRendering()
    {
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnCompositionRendering;
    }

    private void OnCompositionRendering(object? sender, object e)
    {
        if (_viewport == null || _viewport.Camera is not PerspectiveCamera camera) return;

        var pos = camera.Position;
        var look = camera.LookDirection;
        var up = camera.UpDirection;

        if (pos == _lastCameraPos && look == _lastCameraLook && up == _lastCameraUp) return;

        _lastCameraPos = pos;
        _lastCameraLook = look;
        _lastCameraUp = up;

        UpdateHotspotMarkers();
    }

    private FrameworkElement BuildHotspotsToolbar()
    {
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12),
            Spacing = 8,
        };

        _authoringModeButton = new Button
        {
            Content = GetString("Edit Hotspots", "Редактировать точки"),
            FontSize = 12,
            Padding = new Thickness(10, 5, 10, 5),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Rgb(240, 240, 240)),
            Foreground = Brush(51, 51, 51),
        };
        _authoringModeButton.Resources["ButtonBackground"] = new SolidColorBrush(Rgb(240, 240, 240));
        _authoringModeButton.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Rgb(220, 220, 220));
        _authoringModeButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Rgb(200, 200, 200));
        _authoringModeButton.Resources["ButtonForeground"] = Brush(51, 51, 51);
        _authoringModeButton.Click += (s, e) => ToggleAuthoringMode();

        var clearBtn = new Button
        {
            Content = GetString("Clear All", "Очистить все"),
            FontSize = 12,
            Padding = new Thickness(10, 5, 10, 5),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Rgb(240, 240, 240)),
            Foreground = Brush(51, 51, 51),
        };
        clearBtn.Resources["ButtonBackground"] = new SolidColorBrush(Rgb(240, 240, 240));
        clearBtn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Rgb(220, 220, 220));
        clearBtn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Rgb(200, 200, 200));
        clearBtn.Resources["ButtonForeground"] = Brush(51, 51, 51);
        clearBtn.Click += (s, e) => PromptClearAllHotspots();

        toolbar.Children.Add(_authoringModeButton);
        toolbar.Children.Add(clearBtn);

        return toolbar;
    }

    private FrameworkElement BuildHotspotDetailsPanel()
    {
        _hotspotDetailsTitle = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(51, 51, 51),
        };

        _hotspotDetailsDesc = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(102, 102, 102),
            Margin = new Thickness(0, 4, 0, 0),
        };

        var closeBtn = new Button
        {
            Content = new SymbolIcon(Symbol.Cancel) { Width = 12, Height = 12 },
            Background = new SolidColorBrush(WinColors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(4),
            Margin = new Thickness(4),
        };
        closeBtn.Click += (s, e) => _hotspotDetailsPanel.Visibility = Visibility.Collapsed;

        var textStack = new StackPanel
        {
            Children = { _hotspotDetailsTitle, _hotspotDetailsDesc },
            Margin = new Thickness(0, 0, 24, 0)
        };

        var card = new Border
        {
            Background = new SolidColorBrush(WinColors.White),
            BorderBrush = Brush(220, 220, 220),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            MinWidth = 250,
            MaxWidth = 400,
            Child = new Grid
            {
                Children = { textStack, closeBtn }
            }
        };

        _hotspotDetailsPanel = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12, 12, 12, 20),
            Visibility = Visibility.Collapsed,
            Children = { card }
        };

        return _hotspotDetailsPanel;
    }

    private void ToggleAuthoringMode()
    {
        _authoringMode = !_authoringMode;
        if (_authoringMode)
        {
            _hotspotDetailsPanel.Visibility = Visibility.Collapsed;
        }

        _authoringModeButton.Content = _authoringMode
            ? GetString("Exit Edit Mode", "Выйти из ред.")
            : GetString("Edit Hotspots", "Редактировать точки");

        if (_authoringMode)
        {
            _authoringModeButton.Background = Brush(231, 76, 60);
            _authoringModeButton.Foreground = White;
            _authoringModeButton.Resources["ButtonBackground"] = Brush(231, 76, 60);
            _authoringModeButton.Resources["ButtonBackgroundPointerOver"] = Brush(242, 110, 97);
            _authoringModeButton.Resources["ButtonBackgroundPressed"] = Brush(192, 57, 43);
            _authoringModeButton.Resources["ButtonForeground"] = White;
        }
        else
        {
            _authoringModeButton.Background = new SolidColorBrush(Rgb(240, 240, 240));
            _authoringModeButton.Foreground = Brush(51, 51, 51);
            _authoringModeButton.Resources["ButtonBackground"] = new SolidColorBrush(Rgb(240, 240, 240));
            _authoringModeButton.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Rgb(220, 220, 220));
            _authoringModeButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Rgb(200, 200, 200));
            _authoringModeButton.Resources["ButtonForeground"] = Brush(51, 51, 51);
        }
    }

    private void Viewport_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_viewport);
        _pressedPoint = new Vector2((float)pt.Position.X, (float)pt.Position.Y);
        _pressedTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        CancelCameraAnimation();
    }

    private void Viewport_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_pressedPoint.HasValue) return;

        var pt = e.GetCurrentPoint(_viewport);
        var releasePoint = new Vector2((float)pt.Position.X, (float)pt.Position.Y);
        long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _pressedTime;
        float dist = Vector2.Distance(_pressedPoint.Value, releasePoint);

        _pressedPoint = null;

        if (elapsed < 300 && dist < 5)
        {
            if (_authoringMode)
            {
                var hits = _viewport.FindHits(releasePoint);
                if (hits != null && hits.Count > 0)
                {
                    var hit = hits.FirstOrDefault(h => h.ModelHit != null);
                    if (hit != null)
                    {
                        var camera = _viewport.Camera as PerspectiveCamera;
                        if (camera != null)
                        {
                            var anchor = hit.PointHit;
                            var camPos = camera.Position;
                            var camLook = camera.LookDirection;
                            var camUp = camera.UpDirection;
                            ShowAddHotspotPrompt(anchor, camPos, camLook, camUp);
                        }
                    }
                }
            }
        }
    }

    private void ShowAddHotspotPrompt(Vector3 anchor, Vector3 camPos, Vector3 camLook, Vector3 camUp)
    {
        CancelCameraAnimation();

        var titleBox = new TextBox
        {
            Header = GetString("Title", "Название"),
            PlaceholderText = GetString("Enter title...", "Введите название..."),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var descBox = new TextBox
        {
            Header = GetString("Description (optional)", "Описание (необязательно)"),
            PlaceholderText = GetString("Enter description...", "Введите описание..."),
            AcceptsReturn = true,
            Height = 80,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var saveBtn = new Button
        {
            Content = GetString("Save", "Сохранить"),
            Background = Blue,
            Foreground = White,
            Margin = new Thickness(0, 0, 8, 0),
        };
        saveBtn.Resources["ButtonBackground"] = Blue;
        saveBtn.Resources["ButtonBackgroundPointerOver"] = BlueHover;
        saveBtn.Resources["ButtonBackgroundPressed"] = BluePressed;
        saveBtn.Resources["ButtonForeground"] = White;
        saveBtn.Resources["ButtonForegroundPointerOver"] = White;
        saveBtn.Resources["ButtonForegroundPressed"] = White;

        var cancelBtn = new Button
        {
            Content = GetString("Cancel", "Отмена"),
        };

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { saveBtn, cancelBtn }
        };

        var card = new Border
        {
            Background = Cream,
            BorderBrush = Brush(210, 213, 227),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20),
            Width = 320,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Children = {
                    new TextBlock {
                        Text = GetString("Add New Hotspot", "Добавить точку"),
                        FontSize = 16,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 0, 0, 12),
                    },
                    titleBox,
                    descBox,
                    buttonsPanel
                }
            }
        };

        _promptOverlay = new Grid
        {
            Background = new SolidColorBrush(new WinColor { A = 100, R = 0, G = 0, B = 0 }),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { card }
        };

        saveBtn.Click += (s, e) =>
        {
            string title = titleBox.Text.Trim();
            if (string.IsNullOrEmpty(title))
            {
                title = $"{GetString("Hotspot", "Точка")} {_hotspots.Count + 1}";
            }

            var newHotspot = new Hotspot
            {
                Id = Guid.NewGuid().ToString(),
                Number = _hotspots.Count > 0 ? _hotspots.Max(h => h.Number) + 1 : 1,
                Title = title,
                Description = descBox.Text.Trim(),
                Anchor = new[] { anchor.X, anchor.Y, anchor.Z },
                CameraPosition = new[] { camPos.X, camPos.Y, camPos.Z },
                CameraLookDirection = new[] { camLook.X, camLook.Y, camLook.Z },
                CameraUpDirection = new[] { camUp.X, camUp.Y, camUp.Z }
            };

            _hotspots.Add(newHotspot);
            SaveHotspots();
            UpdateHotspotMarkers();
            RemovePromptOverlay();
        };

        cancelBtn.Click += (s, e) => RemovePromptOverlay();

        if (_hotspotCanvas.Parent is Grid parentGrid)
        {
            parentGrid.Children.Add(_promptOverlay);
        }
    }

    private void RemovePromptOverlay()
    {
        if (_promptOverlay != null)
        {
            if (_hotspotCanvas.Parent is Grid parentGrid)
            {
                parentGrid.Children.Remove(_promptOverlay);
            }
            _promptOverlay = null;
        }
    }

    private void PromptClearAllHotspots()
    {
        if (_hotspots.Count == 0) return;

        var cancelBtn = new Button { Content = GetString("No", "Нет"), Margin = new Thickness(8, 0, 0, 0) };
        var confirmBtn = new Button { Content = GetString("Yes", "Да"), Background = ErrorRed, Foreground = White };
        confirmBtn.Resources["ButtonBackground"] = ErrorRed;
        confirmBtn.Resources["ButtonBackgroundPointerOver"] = Brush(192, 57, 43);
        confirmBtn.Resources["ButtonBackgroundPressed"] = Brush(150, 40, 27);
        confirmBtn.Resources["ButtonForeground"] = White;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { confirmBtn, cancelBtn }
        };

        var card = new Border
        {
            Background = Cream,
            BorderBrush = Brush(210, 213, 227),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20),
            Width = 300,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 12,
                Children = {
                    new TextBlock {
                        Text = GetString("Clear All Hotspots?", "Удалить все точки?"),
                        FontSize = 16,
                        FontWeight = FontWeights.SemiBold,
                    },
                    new TextBlock {
                        Text = GetString("This will delete all saved hotspots for this model. Are you sure?", "Это удалит все сохраненные точки для этой модели. Продолжить?"),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                    },
                    buttons
                }
            }
        };

        var overlay = new Grid
        {
            Background = new SolidColorBrush(new WinColor { A = 100, R = 0, G = 0, B = 0 }),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { card }
        };

        cancelBtn.Click += (s, e) =>
        {
            if (_hotspotCanvas.Parent is Grid parentGrid) parentGrid.Children.Remove(overlay);
        };

        confirmBtn.Click += (s, e) =>
        {
            _hotspots.Clear();
            SaveHotspots();
            UpdateHotspotMarkers();
            _hotspotDetailsPanel.Visibility = Visibility.Collapsed;
            if (_hotspotCanvas.Parent is Grid parentGrid) parentGrid.Children.Remove(overlay);
        };

        if (_hotspotCanvas.Parent is Grid parentGrid)
        {
            parentGrid.Children.Add(overlay);
        }
    }

    private void DeleteHotspot(Hotspot hotspot)
    {
        _hotspots.Remove(hotspot);
        for (int i = 0; i < _hotspots.Count; i++)
        {
            _hotspots[i].Number = i + 1;
        }
        SaveHotspots();
        UpdateHotspotMarkers();
        _hotspotDetailsPanel.Visibility = Visibility.Collapsed;
    }

    private string GetHotspotsPath(string modelPath)
    {
        return Path.ChangeExtension(modelPath, ".hotspots.json");
    }

    private void LoadHotspots(string modelPath)
    {
        _hotspots.Clear();
        _currentModelPath = modelPath;
        _hotspotDetailsPanel.Visibility = Visibility.Collapsed;

        var primaryPath = GetHotspotsPath(modelPath);
        var fallbackPath = Path.Combine(AppPaths.ModelsDir, Path.GetFileNameWithoutExtension(modelPath) + ".hotspots.json");

        string? json = null;
        if (File.Exists(primaryPath))
        {
            try
            {
                json = File.ReadAllText(primaryPath);
            }
            catch (Exception ex)
            {
                Log($"Failed to read primary hotspots file: {ex.Message}");
            }
        }

        if (json == null && File.Exists(fallbackPath))
        {
            try
            {
                json = File.ReadAllText(fallbackPath);
            }
            catch (Exception ex)
            {
                Log($"Failed to read fallback hotspots file: {ex.Message}");
            }
        }

        if (json != null)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<Hotspot>>(json);
                if (list != null)
                {
                    _hotspots = list.OrderBy(h => h.Number).ToList();
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to deserialize hotspots: {ex.Message}");
            }
        }

        UpdateHotspotMarkers();
    }

    private void SaveHotspots()
    {
        if (string.IsNullOrEmpty(_currentModelPath)) return;

        var primaryPath = GetHotspotsPath(_currentModelPath);
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(_hotspots, options);

        try
        {
            var dir = Path.GetDirectoryName(primaryPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(primaryPath, json);
        }
        catch (UnauthorizedAccessException)
        {
            try
            {
                var fallbackPath = Path.Combine(AppPaths.ModelsDir, Path.GetFileNameWithoutExtension(_currentModelPath) + ".hotspots.json");
                Directory.CreateDirectory(AppPaths.ModelsDir);
                File.WriteAllText(fallbackPath, json);
                Log($"Saved hotspots to fallback: {fallbackPath}");
            }
            catch (Exception ex)
            {
                Log($"Failed to save fallback hotspots: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to save primary hotspots: {ex.Message}");
        }
    }

    private void UpdateHotspotMarkers()
    {
        if (_hotspotCanvas == null || _viewport == null || _viewport.Camera == null) return;

        _hotspotCanvas.Children.Clear();

        var camera = _viewport.Camera as PerspectiveCamera;
        if (camera == null) return;

        var cameraPos = camera.Position;
        var cameraLook = Vector3.Normalize(camera.LookDirection);

        double scale = _viewport.XamlRoot?.RasterizationScale ?? 1.0;

        foreach (var hotspot in _hotspots)
        {
            if (hotspot.Anchor == null || hotspot.Anchor.Length < 3) continue;

            var anchor = new Vector3(hotspot.Anchor[0], hotspot.Anchor[1], hotspot.Anchor[2]);
            var toAnchor = anchor - cameraPos;
            var dot = Vector3.Dot(toAnchor, cameraLook);
            if (dot <= 0) continue;

            var projected = _viewport.Project(anchor);
            double screenX = projected.X / scale;
            double screenY = projected.Y / scale;

            var btn = new Button
            {
                Content = hotspot.Number.ToString(),
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
            };

            btn.Resources["ButtonBackground"] = Blue;
            btn.Resources["ButtonBackgroundPointerOver"] = BlueHover;
            btn.Resources["ButtonBackgroundPressed"] = BluePressed;
            btn.Resources["ButtonForeground"] = White;
            btn.Resources["ButtonForegroundPointerOver"] = White;
            btn.Resources["ButtonForegroundPressed"] = White;

            ToolTipService.SetToolTip(btn, hotspot.Title);

            btn.Click += (s, e) => FlyToHotspot(hotspot);

            btn.RightTapped += (s, e) =>
            {
                e.Handled = true;
                if (_authoringMode)
                {
                    DeleteHotspot(hotspot);
                }
            };

            Canvas.SetLeft(btn, screenX - 12);
            Canvas.SetTop(btn, screenY - 12);

            _hotspotCanvas.Children.Add(btn);
        }
    }

    private void FlyToHotspot(Hotspot hotspot)
    {
        ShowHotspotDetails(hotspot);

        if (_viewport.Camera is not PerspectiveCamera camera) return;

        if (hotspot.CameraPosition == null || hotspot.CameraPosition.Length < 3 ||
            hotspot.CameraLookDirection == null || hotspot.CameraLookDirection.Length < 3 ||
            hotspot.CameraUpDirection == null || hotspot.CameraUpDirection.Length < 3)
        {
            return;
        }

        var targetPos = new Vector3(hotspot.CameraPosition[0], hotspot.CameraPosition[1], hotspot.CameraPosition[2]);
        var targetLook = new Vector3(hotspot.CameraLookDirection[0], hotspot.CameraLookDirection[1], hotspot.CameraLookDirection[2]);
        var targetUp = new Vector3(hotspot.CameraUpDirection[0], hotspot.CameraUpDirection[1], hotspot.CameraUpDirection[2]);

        CancelCameraAnimation();

        _activeAnimator = new CameraAnimator(camera, targetPos, targetLook, targetUp, 800, () =>
        {
            _headlight.Direction = Vector3.Normalize(camera.LookDirection);
            _activeAnimator = null;
        });
    }

    private void CancelCameraAnimation()
    {
        if (_activeAnimator != null)
        {
            _activeAnimator.Cancel();
            _activeAnimator = null;
        }
    }

    private void ShowHotspotDetails(Hotspot hotspot)
    {
        _hotspotDetailsTitle.Text = $"{hotspot.Number}. {hotspot.Title}";
        _hotspotDetailsDesc.Text = hotspot.Description;
        _hotspotDetailsPanel.Visibility = Visibility.Visible;
    }

    private sealed class CameraAnimator
    {
        private readonly PerspectiveCamera _camera;
        private readonly Vector3 _startPos, _targetPos;
        private readonly Vector3 _startLook, _targetLook;
        private readonly Vector3 _startUp, _targetUp;
        private readonly double _durationMs;
        private readonly System.Diagnostics.Stopwatch _stopwatch;
        private readonly Action? _onComplete;

        public CameraAnimator(PerspectiveCamera camera, Vector3 targetPos, Vector3 targetLook, Vector3 targetUp, double durationMs, Action? onComplete = null)
        {
            _camera = camera;
            _startPos = camera.Position;
            _targetPos = targetPos;
            _startLook = camera.LookDirection;
            _targetLook = targetLook;
            _startUp = camera.UpDirection;
            _targetUp = targetUp;
            _durationMs = durationMs;
            _onComplete = onComplete;
            _stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        }

        private void OnRendering(object? sender, object e)
        {
            double elapsed = _stopwatch.ElapsedMilliseconds;
            double t = Math.Clamp(elapsed / _durationMs, 0.0, 1.0);

            double easeT = t < 0.5 ? 4.0 * t * t * t : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0;

            _camera.Position = Vector3.Lerp(_startPos, _targetPos, (float)easeT);

            Vector3 interpolatedLook = Vector3.Lerp(_startLook, _targetLook, (float)easeT);
            _camera.LookDirection = Vector3.Normalize(interpolatedLook);

            Vector3 interpolatedUp = Vector3.Lerp(_startUp, _targetUp, (float)easeT);
            _camera.UpDirection = Vector3.Normalize(interpolatedUp);

            if (t >= 1.0)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
                _stopwatch.Stop();
                _onComplete?.Invoke();
            }
        }

        public void Cancel()
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            _stopwatch.Stop();
        }
    }
}
