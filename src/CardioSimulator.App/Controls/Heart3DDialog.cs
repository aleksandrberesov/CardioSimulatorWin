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
using HelixToolkit;
using HelixToolkit.Geometry;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Assimp;
using HelixToolkit.SharpDX.Model;
using HelixToolkit.SharpDX.Model.Scene;
using HelixToolkit.WinUI.SharpDX;
using Hmx = HelixToolkit.Maths;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinColor = Windows.UI.Color;
using WinColors = Microsoft.UI.Colors;
using SurfaceMesh = CardioSimulator.Core.Domain.SurfaceMesh;
using EikonalSolver = CardioSimulator.Core.Domain.EikonalSolver;
using EikonalSeed = CardioSimulator.Core.Domain.EikonalSeed;
using EikonalOptions = CardioSimulator.Core.Domain.EikonalOptions;

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
    private Grid _viewportGrid = null!;
    private FrameworkElement _viewportLoading = null!;
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

    // New layout: the description is no longer a fixed middle column but a floating card toggled from
    // the left rail (see BuildDescriptionOverlay / ToggleDescription), and a reference ECG band is
    // pinned along the bottom (BuildEcgStrip). _ecgDrawn* memoise the last strip size so a resize
    // storm doesn't rebuild the (few-hundred-line) grid on every identical SizeChanged tick.
    private Button _descriptionButton = null!;
    private FrameworkElement _descriptionOverlay = null!;
    private double _ecgDrawnW = -1;
    private double _ecgDrawnH = -1;

    // Conduction-system visualisation: a glowing pathway (SA → AV → His → Purkinje) with a
    // travelling depolarisation pulse, plus the "X-ray" translucency that lets it show through the
    // myocardium. See [[ConductionSystem]].
    private SceneNode? _importedRoot;
    private float _modelMaxDim = 1f;
    private MeshGeometryModel3D _conductionPathModel = null!;
    private MeshGeometryModel3D _pulseModel = null!;
    private ConductionPath? _conductionPath;
    private bool _conductionPlaying;
    private readonly System.Diagnostics.Stopwatch _conductionClock = new();
    private int _bpm = 75;
    private bool _transparent;
    private bool _conductionEditMode;

    // Cutaway ("half heart"): a parallel group of cross-section copies of the imported meshes, shown
    // in place of the normal model with a runtime cutting plane the user sweeps. Kept separate so the
    // untouched normal path keeps driving hotspots / X-ray / hit-testing.
    private SceneNodeGroupModel3D _cutRoot = null!;
    private readonly List<CrossSectionMeshNode> _cutNodes = new();
    private bool _cutaway;
    private Hmx.BoundingBox _modelBounds;
    private Button _cutawayButton = null!;

    // Leads scheme ("Схема отведений"): the customer model bundles a human silhouette + ECG lead
    // system/axes/text around the heart. IsolateHeart() hides these for the default heart-only view;
    // this button toggles them back on and reframes to the whole scene. Empty ⇒ plain heart model.
    private readonly List<MeshNode> _scaffoldMeshes = new();
    private bool _leadsSchemeOn;
    private Button _leadsSchemeButton = null!;
    private Vector3 _heartCentroid;
    private Vector3 _sceneCentroid;
    private float _sceneFrameDim = 1f;
    private Slider _cutSlider = null!;
    private FrameworkElement _cutSliderHost = null!;

    private TextBlock _phaseCaption = null!;
    private TextBlock _editHint = null!;
    private Border _phaseCaptionHost = null!;
    private Border _editHintHost = null!;
    private Button _playPauseButton = null!;
    private Button _xrayButton = null!;
    private Button _conductionEditButton = null!;
    private readonly Dictionary<MeshNode, Hmx.Color4> _originalDiffuse = new();
    
    // Wavefront visualisation
    private bool _wavefrontOn;
    private Button _wavefrontButton = null!;
    private readonly Dictionary<MeshNode, float[]> _activationTimes = new();
    private readonly Dictionary<MeshNode, MaterialCore?> _preWavefrontMaterials = new();
    private PhongMaterialCore _wavefrontMaterial = null!;
    // Off-thread eikonal precompute + a small (model, pathway, speed)-keyed cache so reopening the dialog
    // or re-authoring the same pathway does not re-solve. See [[EikonalSolver]] / [[SurfaceMesh]].
    private static readonly Dictionary<string, WavefrontSolution> _wavefrontCache = new();
    private static readonly Queue<string> _wavefrontCacheOrder = new();
    private const int WavefrontCacheCap = 6;
    // Infarct → conduction-block coupling: the infarct progress (bucketed to 0..10) the current
    // activation map was solved with, so we only re-solve when the necrotic (non-conducting) region
    // actually changes. Mask value × progress ≥ this threshold ⇒ that vertex is dead scar.
    private int _wavefrontSolvedInfarctBucket = -1;
    // Necrosis + inner peri-infarct border (both poorly/non-conducting) block the wave; a lower value
    // captures the feathered mask edge so the dead zone is a contiguous barrier, not sparse points.
    private const float InfarctBlockThreshold = 0.4f;
    // Customisable depolarisation colour scheme (Classic = original blue→red). The action-potential
    // phase timings map a per-vertex "intensity" 0..1 that the scheme's colour ramp is sampled with.
    private WavefrontScheme _wavefrontScheme = WavefrontScheme.Classic;
    private ComboBox _wavefrontSchemeCombo = null!;
    private const float ApUpstrokeMs = 10f, ApPlateauMs = 200f, ApRepolMs = 100f;
    // C2: propagation streamlines ("sparkle lines") — short glyphs oriented by ∇(activation) = the wave's
    // travel direction, coloured by activation. Independent overlay, toggled separately from the mesh.
    private bool _streamlinesOn;
    private Button _streamlineButton = null!;
    private LineGeometryModel3D _streamlineModel = null!;
    private float[] _streamlineActivation = Array.Empty<float>();
    // C3: streamline orientation — by wave travel direction (∇activation) or by myocardial fibre
    // architecture (rule-based epicardial model: long-axis Laplace field + helix rotation).
    private StreamlineOrientation _streamlineOrientation = StreamlineOrientation.Propagation;
    private ComboBox _streamlineOrientationCombo = null!;
    private const float FiberHelixAngleDeg = -60f;

    // Infarct visualisation: blends the heart's healthy albedo toward an infarcted one (a black
    // necrosis patch) via a grayscale mask, driven by a 0..1 progress. The blend runs on the CPU
    // (see [[InfarctTextureBlender]] / [[InfarctTextureSet]]) and is uploaded as the material's
    // albedo map; the embedded normal map keeps lighting the relief throughout. Only shown when the
    // model ships the healthy/infarct/mask sidecar textures.
    private InfarctTextureSet? _infarctSet;
    // The textured heart materials. The Assimp importer maps this glTF to Phong (DiffuseMap), but PBR
    // (AlbedoMap) is handled too so the feature survives a different model or importer config.
    private readonly List<MaterialCore> _infarctMaterials = new();
    // The heart-skin meshes that carry the infarct UV atlas, tracked by node identity so the wavefront
    // mask lookup still finds them after the wavefront view swaps their Material out.
    private readonly HashSet<MeshNode> _infarctMeshes = new();
    private readonly Dictionary<MaterialCore, TextureModel?> _originalAlbedo = new();
    private float _infarctProgress;
    private float _appliedInfarctProgress = -1f;      // last progress pushed to the GPU
    private float _lastInfarctBuildProgress = -1f;     // last progress built during animation (throttle)
    private float _infarctStartProgress;               // progress when the current animation began
    private bool _infarctBuilding;
    private float? _pendingInfarctProgress;            // latest target while a build is in flight (coalescing)
    private bool _infarctPlaying;
    private bool _suppressSlider;                       // guard so animation thumb moves don't re-enter the handler
    private readonly System.Diagnostics.Stopwatch _infarctClock = new();
    private const float InfarctDurationSeconds = 6f;
    private Slider _infarctSlider = null!;
    private Button _infarctPlayButton = null!;
    private TextBlock _infarctLabel = null!;
    private FrameworkElement _infarctControls = null!;

    // xamlRoot is unused: the view mounts into the app's own Root grid (see ShowCoreAsync), but the
    // signature is kept so the call site (and the other monitor dialogs) stay uniform. An optional
    // heart rate (from the loaded rhythm) seeds the conduction animation's pace; null ⇒ default.
    public static Task ShowAsync(XamlRoot xamlRoot, int? bpm = null)
    {
        var dialog = new Heart3DDialog();
        if (bpm is { } b && b > 0)
        {
            dialog._bpm = Math.Clamp(b, 40, 180);
        }
        return dialog.ShowCoreAsync();
    }

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
    /// the UI thread, which can stall for a noticeable beat. So the card chrome (title, buttons,
    /// description panel) is built and shown <em>immediately</em> with a spinner + caption over the
    /// viewport region; only after that has painted (a frame ⇒ the compositor animates the spinner
    /// off-thread) is the heavy <see cref="Viewport3DX"/> constructed and slotted in. The dialog opens
    /// at once with a waiting indicator instead of freezing until the 3D device is ready.
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
            StopInfarctPlay();
            StopCompositionRendering();
            // _viewport is null if the user closed during the loading spinner, before it was built.
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

        // Build and show the card chrome up front. The viewport region shows a loading cover
        // (spinner + caption) until the DirectX viewport is constructed below — so the dialog appears
        // instantly with a waiting indicator rather than blocking on the 3D device first.
        var card = BuildCard(Close);
        // Fill most of the window (leaving a backdrop margin), capped so it isn't huge on big monitors.
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        card.VerticalAlignment = VerticalAlignment.Stretch;
        card.Margin = new Thickness(40);
        card.MaxWidth = 1500;
        card.MaxHeight = 1000;
        overlay.Children.Add(card);
        root.Children.Add(overlay); // added last ⇒ on top

        // Let the card + loading cover paint (and hand off to the compositor) before the synchronous
        // viewport / DirectX construction blocks the UI thread.
        await WaitForNextFrameAsync();

        // The user may have tapped the backdrop to dismiss while the spinner was up; if so the overlay
        // is gone — don't build (and leak) the DirectX viewport.
        if (overlay.Parent is null)
        {
            return;
        }

        // Now construct the heavy DirectX viewport and slot it into the card, then load the active
        // model (user override or bundled default). The loading cover stays up throughout.
        BuildAndAttachViewport();
        TryAutoLoadModel();
        StartCompositionRendering();
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // right: viewport fills the rest

        // Left column: function buttons + feature controls, wrapped in a ScrollViewer so the tall stack
        // never clips the card on a short window.
        var left = new StackPanel { Spacing = 10, Width = 190, VerticalAlignment = VerticalAlignment.Top };
        _leadsSchemeButton = FunctionButton(AppStrings.Monitor3DLeadScheme);
        _leadsSchemeButton.Click += (_, _) => ToggleLeadsScheme();
        left.Children.Add(_leadsSchemeButton);
        left.Children.Add(FunctionButton(AppStrings.Monitor3DFunctionFormat(2)));
        left.Children.Add(FunctionButton(AppStrings.Monitor3DFunctionFormat(3)));
        left.Children.Add(FunctionButton(AppStrings.Monitor3DMi));
        left.Children.Add(FunctionButton(AppStrings.Monitor3DFunctionFormat(5)));
        left.Children.Add(FunctionButton(AppStrings.Monitor3DFunctionFormat(6)));
        // The description panel is no longer a fixed middle column; this button toggles it as a floating
        // card over the viewport (see BuildDescriptionOverlay / ToggleDescription).
        _descriptionButton = FunctionButton(GetString("Description", "Описание"));
        _descriptionButton.Click += (_, _) => ToggleDescription();
        left.Children.Add(_descriptionButton);
        left.Children.Add(BuildConductionControls());
        left.Children.Add(BuildCutawayControls());
        left.Children.Add(BuildInfarctControls());

        var leftScroll = new ScrollViewer
        {
            Content = left,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetColumn(leftScroll, 0);
        grid.Children.Add(leftScroll);

        // Right column: the 3D viewport fills the available space (Star row), with the (error-only)
        // status line and the reference ECG strip stacked beneath it.
        var right = new Grid { RowSpacing = 12 };
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // The heavy DirectX Viewport3DX is not built here — it is constructed and inserted at index 0
        // later (BuildAndAttachViewport), once the card has painted. Until then the loading cover below
        // (added last, so it's on top) fills this region with a spinner + caption.
        _viewportGrid = new Grid();

        _hotspotCanvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _viewportGrid.Children.Add(_hotspotCanvas);

        var toolbar = BuildHotspotsToolbar();
        _viewportGrid.Children.Add(toolbar);

        var details = BuildHotspotDetailsPanel();
        _viewportGrid.Children.Add(details);

        // Conduction phase caption (top-centre, only while playing) and the authoring hint that names
        // the next conduction node to place.
        _phaseCaption = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = White,
        };
        _phaseCaptionHost = new Border
        {
            Background = new SolidColorBrush(new WinColor { A = 190, R = 30, G = 30, B = 30 }),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = _phaseCaption,
        };
        _viewportGrid.Children.Add(_phaseCaptionHost);

        _editHint = new TextBlock
        {
            FontSize = 13,
            Foreground = White,
        };
        _editHintHost = new Border
        {
            Background = new SolidColorBrush(new WinColor { A = 190, R = 43, G = 108, B = 176 }),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 52, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = _editHint,
        };
        _viewportGrid.Children.Add(_editHintHost);

        // Floating description card (hidden until the Description button is pressed): above the model
        // overlays but below the loading cover, so a load still fully covers it.
        _viewportGrid.Children.Add(BuildDescriptionOverlay());

        // Opaque loading cover: shown from the moment the card opens (while the DirectX viewport is
        // being constructed) and kept up while a model imports, so the DirectX surface (and the red
        // fallback sphere) never shows through during the load — just a spinner + caption.
        _viewportLoading = new Border
        {
            Background = White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Visible,
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 10,
                Children =
                {
                    new ProgressRing { IsActive = true, Width = 40, Height = 40 },
                    new TextBlock
                    {
                        Text = AppStrings.Monitor3DLoading,
                        FontSize = 13,
                        Foreground = InfoGray,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };
        _viewportGrid.Children.Add(_viewportLoading);

        var viewportFrame = new Border
        {
            Background = White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = _viewportGrid,
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

        var ecgStrip = BuildEcgStrip();
        Grid.SetRow(ecgStrip, 2);
        right.Children.Add(ecgStrip);

        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        return grid;
    }

    /// <summary>
    /// The floating description card that replaces the old fixed middle column. Same text ("what is
    /// happening" + "or a 12-lead ECG window") on the design blue, anchored to the viewport's
    /// bottom-left with a close button, shown/hidden by the left-rail Description button.
    /// </summary>
    private FrameworkElement BuildDescriptionOverlay()
    {
        var closeBtn = new Button
        {
            Content = new SymbolIcon(Symbol.Cancel) { Width = 12, Height = 12 },
            Background = new SolidColorBrush(WinColors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = White,
        };
        closeBtn.Click += (_, _) => ToggleDescription(false);

        var texts = new StackPanel { Spacing = 12, Margin = new Thickness(0, 2, 22, 0) };
        texts.Children.Add(PanelText(AppStrings.Monitor3DDescription));
        texts.Children.Add(PanelText(AppStrings.Monitor3DOrEcg));

        var card = new Border
        {
            Background = Blue,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 14, 18, 18),
            MaxWidth = 320,
            Child = new Grid { Children = { texts, closeBtn } },
        };

        _descriptionOverlay = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16),
            Visibility = Visibility.Collapsed,
            Children = { card },
        };
        return _descriptionOverlay;
    }

    /// <summary>Shows/hides the floating description card and keeps the rail button label in sync.</summary>
    private void ToggleDescription(bool? show = null)
    {
        bool visible = show ?? (_descriptionOverlay.Visibility != Visibility.Visible);
        _descriptionOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _descriptionButton.Content = visible
            ? GetString("Hide description", "Скрыть описание")
            : GetString("Description", "Описание");
    }

    /// <summary>
    /// The reference ECG band pinned along the bottom of the dialog. This is NOT the live monitor
    /// trace — the dialog is handed only a heart rate, not sample data — so it draws pink ECG paper
    /// (the same grid palette the app's ECG figure renderer uses) with a clean normal-sinus PQRST
    /// trace paced to the selected rate, as a visual anchor matching the design. Redrawn on resize to
    /// refill the width.
    /// </summary>
    private FrameworkElement BuildEcgStrip()
    {
        var canvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        canvas.SizeChanged += (_, _) => DrawEcgStrip(canvas);

        var leadLabel = new Border
        {
            Background = new SolidColorBrush(new WinColor { A = 220, R = 255, G = 245, B = 245 }),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Child = new TextBlock
            {
                Text = AppStrings.Monitor3DEcgLead,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(0x86, 0x2A, 0x2A),
            },
        };

        return new Border
        {
            Height = 96,
            CornerRadius = new CornerRadius(8),
            Background = Brush(0xFF, 0xF5, 0xF5),   // pink ECG paper (mirrors EcgSvgRenderer.GridBg)
            Child = new Grid { Children = { canvas, leadLabel } },
        };
    }

    /// <summary>Draws the ECG-paper grid + a normal-sinus reference trace across the strip's width.</summary>
    private void DrawEcgStrip(Canvas canvas)
    {
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }
        // Skip redundant redraws — SizeChanged fires repeatedly with the same size during layout.
        if (Math.Abs(w - _ecgDrawnW) < 0.5 && Math.Abs(h - _ecgDrawnH) < 0.5)
        {
            return;
        }
        _ecgDrawnW = w;
        _ecgDrawnH = h;

        canvas.Children.Clear();

        var small = Brush(0xFD, 0xE4, 0xE4);   // 1 mm grid  (mirrors EcgSvgRenderer.GridSmall)
        var large = Brush(0xF9, 0xBD, 0xBD);   // 5 mm grid  (mirrors EcgSvgRenderer.GridLarge)
        const double cell = 6.0;               // 1 mm at 6 px/mm, the app's fixed figure scale
        const double bold = cell * 5;          // 5 mm

        for (double x = 0; x <= w; x += cell)
        {
            bool isBold = x % bold < 0.5;
            canvas.Children.Add(GridLine(x, 0, x, h, isBold ? large : small, isBold ? 1.0 : 0.5));
        }
        for (double y = 0; y <= h; y += cell)
        {
            bool isBold = y % bold < 0.5;
            canvas.Children.Add(GridLine(0, y, w, y, isBold ? large : small, isBold ? 1.0 : 0.5));
        }

        double baseline = h * 0.62;
        double ampPx = h * 0.42;
        const double pxPerSec = 25.0 * 6.0;   // 25 mm/s * 6 px/mm (matches the ECG figure scale)
        double cycle = pxPerSec * 60.0 / Math.Clamp(_bpm, 20, 300);
        const double left = 70;                // start past the lead label

        var points = new Microsoft.UI.Xaml.Media.PointCollection();
        for (double x = left; x <= w - 4; x += 1.5)
        {
            double f = ((x - left) % cycle) / cycle;
            double y = baseline - EcgWave(f) * ampPx;
            points.Add(new Windows.Foundation.Point(x, y));
        }
        canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Polyline
        {
            Stroke = Brush(0x11, 0x11, 0x11),   // mirrors EcgSvgRenderer.TraceColor
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            Points = points,
        });
    }

    private static Microsoft.UI.Xaml.Shapes.Line GridLine(
        double x1, double y1, double x2, double y2, SolidColorBrush brush, double thickness)
        => new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness };

    /// <summary>One normal-sinus PQRST cycle as a sum of Gaussian bumps; phase 0..1 → amplitude ≈ -0.25..1.</summary>
    private static double EcgWave(double f)
    {
        double G(double mu, double sig) => Math.Exp(-((f - mu) * (f - mu)) / (2 * sig * sig));
        double p =  0.12 * G(0.12, 0.022);
        double q = -0.07 * G(0.30, 0.008);
        double r =  1.00 * G(0.335, 0.010);
        double s = -0.22 * G(0.37, 0.011);
        double t =  0.26 * G(0.58, 0.032);
        return p + q + r + s + t;
    }

    /// <summary>
    /// Constructs the heavy DirectX viewport (this is the part that stalls the UI thread) and slots it
    /// into the already-visible card, beneath the overlay layers (hotspot canvas, toolbar, loading
    /// cover) so those stay on top. Called after the card has painted its waiting indicator.
    /// </summary>
    private void BuildAndAttachViewport()
    {
        var viewport = BuildHeartViewport();
        _viewportGrid.Children.Insert(0, viewport);
        viewport.PointerPressed += Viewport_PointerPressed;
        viewport.PointerReleased += Viewport_PointerReleased;
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

        // Parallel container holding cross-section (cuttable) copies of the imported meshes; only
        // rendered while cutaway mode is on (the normal _modelRoot is hidden then).
        _cutRoot = new SceneNodeGroupModel3D { IsRendering = false };
        _viewport.Items.Add(_cutRoot);

        // Placeholder primitive (heart stand-in). Hidden by default — it is only the fallback when
        // there is no model to load / a load fails, NEVER while a model is importing (otherwise the
        // red sphere flashes during the load). TryAutoLoadModel/LoadModelAsync turn it on.
        var builder = new MeshBuilder(true, true, false);
        builder.AddSphere(new Vector3(0, 0, 0), 2.2f, 48, 48);
        _placeholder = new MeshGeometryModel3D
        {
            Geometry = builder.ToMeshGeometry3D(),
            Material = PhongMaterials.Red,
            IsRendering = false,
        };
        _viewport.Items.Add(_placeholder);

        // Conduction pathway (gold, self-lit so it reads through a translucent heart) and the
        // travelling depolarisation pulse (bright, strongly emissive). Both are hidden until a path
        // is loaded, and neither is hit-testable so it can't intercept authoring clicks on the model.
        _conductionPathModel = new MeshGeometryModel3D
        {
            Material = new PhongMaterial
            {
                DiffuseColor = new Hmx.Color4(0.95f, 0.75f, 0.15f, 1f),
                EmissiveColor = new Hmx.Color4(0.45f, 0.33f, 0.05f, 1f),
            },
            IsHitTestVisible = false,
            IsRendering = false,
        };
        _viewport.Items.Add(_conductionPathModel);

        _pulseModel = new MeshGeometryModel3D
        {
            Material = new PhongMaterial
            {
                DiffuseColor = new Hmx.Color4(1f, 0.95f, 0.35f, 1f),
                EmissiveColor = new Hmx.Color4(1f, 0.88f, 0.2f, 1f),
            },
            IsHitTestVisible = false,
            IsRendering = false,
        };
        _viewport.Items.Add(_pulseModel);

        // Wavefront streamlines overlay: per-vertex-coloured line glyphs; geometry is set after a solve.
        _streamlineModel = new LineGeometryModel3D
        {
            Thickness = 1.6,
            Color = WinColors.White, // per-vertex colours carry the wave; white so they show unmodulated
            IsHitTestVisible = false,
            IsRendering = false,
        };
        _viewport.Items.Add(_streamlineModel);

        return _viewport;
    }

    /// <summary>Loads the active model (user override or bundled default); none ⇒ show the placeholder.</summary>
    private void TryAutoLoadModel()
    {
        var path = HeartModelStore.ResolveActiveModelPath();
        if (path is not null)
        {
            // The loading cover is already up from card-open; LoadModelAsync clears it when done.
            _viewportLoading.Visibility = Visibility.Visible;
            _ = LoadModelAsync(path);
        }
        else
        {
            // No model available at all — the red placeholder is the intended fallback; drop the cover.
            _placeholder.IsRendering = true;
            _viewportLoading.Visibility = Visibility.Collapsed;
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
                return new ImportedModel(root, centroid, maxDim, bound);
            });

            if (imported is null)
            {
                SetMessage(AppStrings.Monitor3DLoadFailed, isError: true);
                Log($"FAILED (no scene root): {path}");
                _placeholder.IsRendering = true; // fall back to the placeholder on failure
                return;
            }

            _modelRoot.Clear();
            _modelRoot.AddNode(imported.Root);
            _placeholder.IsRendering = false;
            _importedRoot = imported.Root;

            // The customer model ships a whole ECG teaching scene (human silhouette + ECG lead
            // system/axes/text wrapped around a comparatively tiny heart). In the "3D сердце" dialog we
            // want the heart itself, so hide the scaffolding and frame on the heart's own bounds. A
            // plain heart model (no such meshes) returns null here and keeps whole-scene framing.
            var heart = IsolateHeart(imported.Root);
            _heartCentroid = heart?.centroid ?? imported.Centroid;
            _modelMaxDim = heart?.maxDim ?? imported.MaxDim;
            _modelBounds = heart?.bounds ?? imported.Bounds;
            _sceneCentroid = imported.Centroid;   // whole-scene framing, restored when the leads scheme is shown
            _sceneFrameDim = imported.MaxDim;
            InitLeadsScheme();
            BuildCutRepresentation(imported.Root);
            FrameCamera(_heartCentroid, _modelMaxDim);
            LoadHotspots(path);
            LoadConduction(path, _modelBounds);
            SetupInfarct(imported.Root, path);
            // A newly-loaded model comes in opaque; keep the X-ray toggle state consistent.
            if (_transparent)
            {
                ApplyTransparency(true);
            }
        }
        catch (Exception ex)
        {
            SetMessage($"{AppStrings.Monitor3DLoadFailed}: {ex.Message}", isError: true);
            Log($"EXCEPTION: {path}\n{ex}");
            _placeholder.IsRendering = true; // fall back to the placeholder on failure
        }
        finally
        {
            _busy = false;
            _viewportLoading.Visibility = Visibility.Collapsed;
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
    private sealed record ImportedModel(SceneNode Root, Vector3 Centroid, float MaxDim, Hmx.BoundingBox Bounds);

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

        // Advance the conduction pulse every frame (independent of camera movement).
        AdvanceConduction();

        // Advance the infarct animation (if playing), independent of camera movement.
        AdvanceInfarct();

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
            if (_conductionEditMode)
            {
                var hits = _viewport.FindHits(releasePoint);
                var hit = hits?.FirstOrDefault(h => h.ModelHit != null);
                if (hit != null)
                {
                    PlaceNextConductionNode(hit.PointHit);
                }
                return;
            }
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

        // Tapping the dimmed backdrop outside the card dismisses the prompt (same as Cancel). A tap
        // on the card bubbles up with a deeper OriginalSource and is ignored.
        _promptOverlay.Tapped += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, _promptOverlay))
            {
                RemovePromptOverlay();
            }
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

        // Tapping the dimmed backdrop outside the card dismisses the confirm (same as "No"). A tap on
        // the card bubbles up with a deeper OriginalSource and is ignored.
        overlay.Tapped += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, overlay) && _hotspotCanvas.Parent is Grid parentGrid)
            {
                parentGrid.Children.Remove(overlay);
            }
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

    // ---- Conduction system: controls, loading, animation, authoring, X-ray ----

    /// <summary>Left-column group: play/pause, rate, X-ray toggle, and pathway authoring.</summary>
    private FrameworkElement BuildConductionControls()
    {
        var header = new TextBlock
        {
            Text = GetString("Conduction system", "Проводящая система"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 2),
            TextWrapping = TextWrapping.Wrap,
        };

        _playPauseButton = FunctionButton(GetString("▶ Play", "▶ Пуск"));
        _playPauseButton.Click += (_, _) => ToggleConductionPlay();

        var rateLabel = new TextBlock { FontSize = 12, Foreground = InfoGray };
        void UpdateRateLabel() => rateLabel.Text = GetString($"Rate: {_bpm} bpm", $"ЧСС: {_bpm} уд/мин");
        UpdateRateLabel();

        var rateSlider = new Slider
        {
            Minimum = 40,
            Maximum = 180,
            Value = _bpm,
            StepFrequency = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        rateSlider.ValueChanged += (_, e) =>
        {
            _bpm = (int)Math.Round(e.NewValue);
            UpdateRateLabel();
        };

        _xrayButton = FunctionButton(GetString("X-ray view", "Просвечивание"));
        _xrayButton.Click += (_, _) => ToggleTransparency();

        _wavefrontButton = FunctionButton(GetString("Wavefront view", "Волны деполяризации"));
        _wavefrontButton.Click += (_, _) => ToggleWavefront();

        // Depolarisation colour scheme picker (blue→red classic, thermal, viridis, …).
        _wavefrontSchemeCombo = new ComboBox
        {
            Header = GetString("Wave colours", "Цвета волны"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (WavefrontScheme s in Enum.GetValues<WavefrontScheme>())
        {
            var (en, ru) = SchemeName(s);
            _wavefrontSchemeCombo.Items.Add(new ComboBoxItem { Content = GetString(en, ru), Tag = s });
        }
        _wavefrontSchemeCombo.SelectedIndex = (int)_wavefrontScheme;
        _wavefrontSchemeCombo.SelectionChanged += (_, _) =>
        {
            if (_wavefrontSchemeCombo.SelectedItem is ComboBoxItem { Tag: WavefrontScheme s })
            {
                _wavefrontScheme = s;
            }
        };

        _streamlineButton = FunctionButton(GetString("Streamlines", "Линии волны"));
        _streamlineButton.Click += (_, _) => ToggleStreamlines();

        // Streamline orientation: by wave-travel direction, or by (rule-based) fibre architecture.
        _streamlineOrientationCombo = new ComboBox
        {
            Header = GetString("Line orientation", "Ориентация линий"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (StreamlineOrientation o in Enum.GetValues<StreamlineOrientation>())
        {
            var (en, ru) = OrientationName(o);
            _streamlineOrientationCombo.Items.Add(new ComboBoxItem { Content = GetString(en, ru), Tag = o });
        }
        _streamlineOrientationCombo.SelectedIndex = (int)_streamlineOrientation;
        _streamlineOrientationCombo.SelectionChanged += (_, _) =>
        {
            if (_streamlineOrientationCombo.SelectedItem is ComboBoxItem { Tag: StreamlineOrientation o }
                && o != _streamlineOrientation)
            {
                _streamlineOrientation = o;
                PrecomputeWavefront(); // rebuild the glyphs with the new orientation (cached per orientation)
            }
        };

        _conductionEditButton = FunctionButton(GetString("Edit pathway", "Ред. путь"));
        _conductionEditButton.Click += (_, _) => ToggleConductionEdit();

        return new StackPanel
        {
            Spacing = 8,
            Children = { header, _playPauseButton, rateLabel, rateSlider, _xrayButton, _wavefrontButton, _wavefrontSchemeCombo, _streamlineButton, _streamlineOrientationCombo, _conductionEditButton },
        };
    }

    /// <summary>Left-column group: the "half heart" cutaway toggle and its cut-position sweep.</summary>
    private FrameworkElement BuildCutawayControls()
    {
        var header = new TextBlock
        {
            Text = GetString("Cutaway (half heart)", "Разрез (половина)"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 2),
            TextWrapping = TextWrapping.Wrap,
        };

        _cutawayButton = FunctionButton(GetString("Cut in half", "Разрезать"));
        _cutawayButton.Click += (_, _) => ToggleCutaway();

        _cutSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 50,
            StepFrequency = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _cutSlider.ValueChanged += (_, e) => UpdateCutPlane(e.NewValue / 100.0);

        _cutSliderHost = new StackPanel
        {
            Spacing = 4,
            Visibility = Visibility.Collapsed,
            Children =
            {
                new TextBlock { Text = GetString("Cut position", "Положение разреза"), FontSize = 12, Foreground = InfoGray },
                _cutSlider,
            },
        };

        return new StackPanel { Spacing = 8, Children = { header, _cutawayButton, _cutSliderHost } };
    }

    /// <summary>
    /// Builds the cross-section copy of the imported meshes: one <see cref="CrossSectionMeshNode"/>
    /// per mesh, reusing the same geometry + material (so textures/PBR carry over) with the world
    /// transform baked into the node. Cutting is off until the user enables cutaway; the cut cap is
    /// filled with a muted red so a hollow shell still reads as solid tissue when sliced.
    /// </summary>
    private void BuildCutRepresentation(SceneNode importedRoot)
    {
        _cutRoot.Clear();
        _cutNodes.Clear();
        var capColor = new Hmx.Color4(0.72f, 0.20f, 0.20f, 1f);
        TraverseMeshes(importedRoot, mesh =>
        {
            if (!mesh.Visible)
            {
                return; // skip the hidden scaffolding (silhouette/ECG) so cutaway only slices the heart
            }
            var node = new CrossSectionMeshNode
            {
                Geometry = mesh.Geometry,
                Material = mesh.Material,
                ModelMatrix = mesh.TotalModelMatrix,
                CuttingOperation = CuttingOperation.Intersect,
                CrossSectionColor = capColor,
                EnablePlane1 = false,
            };
            _cutNodes.Add(node);
            _cutRoot.AddNode(node);
        });
        // A freshly loaded model always starts whole.
        _cutaway = false;
        _cutRoot.IsRendering = false;
        _modelRoot.IsRendering = true;
        if (_cutSliderHost is not null)
        {
            _cutSliderHost.Visibility = Visibility.Collapsed;
        }
        if (_cutawayButton is not null)
        {
            _cutawayButton.Content = GetString("Cut in half", "Разрезать");
        }
    }

    /// <summary>Switches between the whole model and the cross-section (cut) representation.</summary>
    private void ToggleCutaway()
    {
        if (_importedRoot is null || _cutNodes.Count == 0)
        {
            return;
        }
        _cutaway = !_cutaway;
        _modelRoot.IsRendering = !_cutaway;
        _cutRoot.IsRendering = _cutaway;
        _cutSliderHost.Visibility = _cutaway ? Visibility.Visible : Visibility.Collapsed;
        _cutawayButton.Content = _cutaway
            ? GetString("Whole heart", "Целое сердце")
            : GetString("Cut in half", "Разрезать");
        if (_cutaway)
        {
            UpdateCutPlane(_cutSlider.Value / 100.0);
        }
    }

    /// <summary>Positions the cutting plane; <paramref name="s"/> sweeps it front-to-back (0..1).</summary>
    private void UpdateCutPlane(double s)
    {
        if (_cutNodes.Count == 0)
        {
            return;
        }
        var min = _modelBounds.Minimum;
        var max = _modelBounds.Maximum;
        // Cut perpendicular to the model's depth (Z): keep the far half, revealing the interior that
        // faces the default camera. The sweep moves the plane through the model's depth.
        var normal = new Vector3(0, 0, 1);
        float cutZ = min.Z + (float)s * (max.Z - min.Z);
        var point = new Vector3((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, cutZ);
        var plane = new System.Numerics.Plane(normal, -Vector3.Dot(normal, point));
        foreach (var node in _cutNodes)
        {
            node.EnablePlane1 = true;
            node.Plane1 = plane;
        }
    }

    /// <summary>Loads the authored pathway for the model (or seeds a default) and draws it.</summary>
    private void LoadConduction(string modelPath, Hmx.BoundingBox bounds)
    {
        StopConduction();
        _conductionPath = ConductionPath.Load(modelPath) ?? ConductionPath.CreateDefault(bounds);
        RebuildConductionGeometry();
        PrecomputeWavefront();
    }

    /// <summary>Rebuilds the static pathway glyph from the current nodes, scaled to the model size.</summary>
    private void RebuildConductionGeometry()
    {
        if (_conductionPath is { IsComplete: true } path)
        {
            float nodeRadius = Math.Max(_modelMaxDim * 0.02f, 0.001f);
            float tubeRadius = Math.Max(_modelMaxDim * 0.008f, 0.0005f);
            _conductionPathModel.Geometry = ConductionPath.BuildPathGeometry(path.Nodes, tubeRadius, nodeRadius);
            _conductionPathModel.IsRendering = true;
        }
        else
        {
            _conductionPathModel.IsRendering = false;
            _pulseModel.IsRendering = false;
        }
    }

    private void ToggleConductionPlay()
    {
        if (_conductionPath is not { IsComplete: true })
        {
            return;
        }
        _conductionPlaying = !_conductionPlaying;
        if (_conductionPlaying)
        {
            _conductionClock.Restart();
            _playPauseButton.Content = GetString("⏸ Pause", "⏸ Пауза");
        }
        else
        {
            _conductionClock.Stop();
            _playPauseButton.Content = GetString("▶ Play", "▶ Пуск");
            _pulseModel.IsRendering = false;
            SetPhaseCaption(null);
        }
    }

    private void StopConduction()
    {
        _conductionPlaying = false;
        _conductionClock.Reset();
        if (_playPauseButton is not null)
        {
            _playPauseButton.Content = GetString("▶ Play", "▶ Пуск");
        }
        if (_pulseModel is not null)
        {
            _pulseModel.IsRendering = false;
        }
        SetPhaseCaption(null);
    }

    /// <summary>Advances the travelling pulse; called every composition frame while playing.</summary>
    private void AdvanceConduction()
    {
        if (!_conductionPlaying || _conductionPath is not { IsComplete: true } path)
        {
            return;
        }
        float cycle = 60000f / Math.Clamp(_bpm, 20, 300);
        float t = (float)(_conductionClock.Elapsed.TotalMilliseconds % cycle);
        var pos = path.Sample(t, out int stageIndex);
        if (pos is { } p)
        {
            float radius = Math.Max(_modelMaxDim * 0.03f, 0.0015f);
            _pulseModel.Geometry = ConductionPath.BuildPulseGeometry(p, radius);
            _pulseModel.IsRendering = true;
            if (stageIndex >= 0 && stageIndex < path.Nodes.Count)
            {
                SetPhaseCaption(PhaseTextForKey(path.Nodes[stageIndex].Key));
            }
        }
        else
        {
            // Electrical diastole between beats — hide the pulse, keep the pathway.
            _pulseModel.IsRendering = false;
            SetPhaseCaption(GetString("Diastole", "Диастола"));
        }

        if (_wavefrontOn)
        {
            AdvanceWavefront(t, cycle);
        }
        if (_streamlinesOn)
        {
            AdvanceStreamlines(t, cycle);
        }
    }

    private static string PhaseTextForKey(string key)
    {
        foreach (var stage in ConductionPath.Template)
        {
            if (stage.Key == key)
            {
                return GetString(stage.PhaseEn, stage.PhaseRu);
            }
        }
        return string.Empty;
    }

    private void SetPhaseCaption(string? text)
    {
        if (_phaseCaptionHost is null)
        {
            return;
        }
        if (string.IsNullOrEmpty(text))
        {
            _phaseCaptionHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            _phaseCaption.Text = text;
            _phaseCaptionHost.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Computes per-vertex activation (depolarisation-arrival) times for the wavefront view by solving the
    /// eikonal equation across the myocardial surface (<see cref="EikonalSolver"/>), seeded from the
    /// conduction-system nodes. Replaces the old straight-line-distance estimate, so the wave now travels
    /// along the tissue — around cavities, and (via the speed field) blockable by scar — instead of through
    /// empty space. Geometry is snapshotted on the UI thread; the heavy weld + solve runs off-thread; the
    /// result is scattered back per mesh and cached by (model, pathway) so reopening the dialog is instant.
    /// </summary>
    private void PrecomputeWavefront()
    {
        _activationTimes.Clear();
        if (_conductionPath is not { IsComplete: true } || _importedRoot is null)
        {
            return;
        }
        var rootAtGather = _importedRoot;

        // Snapshot each heart mesh's world-space vertices, triangle indices and UVs on the UI thread (the
        // only place the HelixToolkit scene may be touched); everything below runs on a copy off-thread.
        var meshes = new List<MeshNode>();
        var meshData = new List<MeshSnapshot>();
        TraverseMeshes(_importedRoot, mesh =>
        {
            if (!mesh.Visible)
            {
                return;
            }
            var name = (mesh.Name ?? string.Empty).ToLowerInvariant();
            if (NonHeartMeshTokens.Any(token => name.Contains(token)))
            {
                return;
            }
            if (mesh.Geometry is not HelixToolkit.SharpDX.MeshGeometry3D geom || geom.Positions is null)
            {
                return;
            }

            var matrix = mesh.TotalModelMatrix;
            int count = geom.Positions.Count;
            var world = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                world[i] = Vector3.Transform(geom.Positions[i], matrix);
            }

            int[] indices;
            if (geom.Indices is { Count: > 0 })
            {
                indices = new int[geom.Indices.Count];
                for (int i = 0; i < indices.Length; i++)
                {
                    indices[i] = geom.Indices[i];
                }
            }
            else
            {
                // Non-indexed triangle list: synthesise sequential indices so the weld can reconnect it.
                int usable = count - (count % 3);
                indices = new int[usable];
                for (int i = 0; i < usable; i++)
                {
                    indices[i] = i;
                }
            }

            // UVs (for infarct-mask sampling) — only meshes carrying the infarct atlas need them.
            // Tracked by node identity so this still holds once the wavefront swaps the mesh's Material.
            bool isInfarct = _infarctMeshes.Contains(mesh);
            Vector2[]? uvs = null;
            if (isInfarct && geom.TextureCoordinates is { } tex && tex.Count == count)
            {
                uvs = new Vector2[count];
                for (int i = 0; i < count; i++)
                {
                    uvs[i] = tex[i];
                }
            }

            meshes.Add(mesh);
            meshData.Add(new MeshSnapshot(world, indices, uvs, isInfarct));
        });

        if (meshes.Count == 0)
        {
            return;
        }

        var seeds = _conductionPath.Nodes.Select(n => (n.Position, n.ArrivalMs)).ToArray();
        float defaultSpeed = Math.Max(_modelMaxDim / 100f, 1e-4f); // ~100 ms to cross the heart
        float weldEps = Math.Max(_modelMaxDim * 1e-4f, 1e-6f);

        // Infarct → conduction block: sample the necrosis mask per vertex and mark dead scar as
        // non-conducting, so the wavefront routes around it. Bucketed so we cache/re-solve in steps.
        int infarctBucket = InfarctBucket();
        _wavefrontSolvedInfarctBucket = infarctBucket;
        InfarctBlockInput? infarct = infarctBucket > 0 && _infarctSet is { } set
            ? new InfarctBlockInput(set, infarctBucket / 10f, InfarctBlockThreshold)
            : null;
        var orientation = _streamlineOrientation;
        string cacheKey = BuildWavefrontCacheKey(_currentModelPath, defaultSpeed, _conductionPath, infarctBucket, orientation);

        _ = SolveAndApplyWavefrontAsync(rootAtGather, meshes, meshData, seeds, defaultSpeed, weldEps, _modelMaxDim, infarct, orientation, cacheKey);
    }

    /// <summary>Current infarct necrosis quantised to 0..10 (0 when the model has no infarct maps).</summary>
    private int InfarctBucket() =>
        _infarctSet is null ? 0 : (int)(Math.Clamp(_infarctProgress, 0f, 1f) * 10f + 0.5f);

    /// <summary>Re-solves the wavefront when the necrotic region has grown/shrunk enough to matter.</summary>
    private void MaybeResolveWavefrontForInfarct()
    {
        if (_wavefrontOn && InfarctBucket() != _wavefrontSolvedInfarctBucket)
        {
            PrecomputeWavefront();
        }
    }

    /// <summary>A heart mesh snapshot handed to the off-thread solver (no HelixToolkit types).</summary>
    private sealed record MeshSnapshot(Vector3[] Positions, int[] Indices, Vector2[]? Uvs, bool IsInfarct);

    /// <summary>Infarct mask + quantised progress + threshold for marking non-conducting scar vertices.</summary>
    private sealed record InfarctBlockInput(InfarctTextureSet Mask, float Progress, float Threshold);

    /// <summary>Off-thread solve result: per-mesh activation times + the streamline glyph geometry.</summary>
    private sealed record WavefrontSolution(List<float[]> MeshTimes, Vector3[] LinePositions, float[] LineActivation);

    /// <summary>
    /// Runs (or reuses a cached) eikonal solve off the UI thread, then scatters the activation times back
    /// onto the live meshes — but only if the same model is still loaded (a solve may outlive a model swap
    /// or dialog close). Re-applies the wavefront material if the view was toggled on while solving.
    /// </summary>
    private async Task SolveAndApplyWavefrontAsync(
        SceneNode rootAtGather,
        List<MeshNode> meshes,
        List<MeshSnapshot> meshData,
        (Vector3 position, float arrivalMs)[] seeds,
        float defaultSpeed,
        float weldEps,
        float lengthScale,
        InfarctBlockInput? infarct,
        StreamlineOrientation orientation,
        string cacheKey)
    {
        try
        {
            WavefrontSolution? solution = null;
            if (_wavefrontCache.TryGetValue(cacheKey, out var cached)
                && cached.MeshTimes.Count == meshData.Count
                && !cached.MeshTimes.Where((t, i) => t.Length != meshData[i].Positions.Length).Any())
            {
                solution = cached;
            }

            if (solution is null)
            {
                solution = await Task.Run(() => SolveWavefront(meshData, seeds, defaultSpeed, weldEps, lengthScale, infarct, orientation));
                StoreWavefrontCache(cacheKey, solution);
            }

            if (!ReferenceEquals(_importedRoot, rootAtGather))
            {
                return; // model swapped or dialog closed while solving — discard stale result
            }
            for (int m = 0; m < meshes.Count; m++)
            {
                _activationTimes[meshes[m]] = solution.MeshTimes[m];
            }
            ApplyStreamlineGeometry(solution.LinePositions, solution.LineActivation);
            if (_wavefrontOn)
            {
                ApplyWavefrontMaterials();
            }
        }
        catch (Exception ex)
        {
            // Fire-and-forget: never let a wavefront-solve failure escape as an unobserved task exception.
            Log($"Wavefront solve failed: {ex}");
        }
    }

    /// <summary>
    /// Off-thread core: welds every heart mesh into one graph (shared seams connect; separate meshes stay
    /// separate connected components), seeds it from the conduction nodes, solves the eikonal equation, and
    /// scatters the activation times back per mesh. Each node is seeded at its nearest welded vertex with
    /// the straight-line gap folded into the ignition offset, so a node far from a mesh does not light it
    /// early — geodesic propagation across the surface takes over from there.
    /// </summary>
    private static WavefrontSolution SolveWavefront(
        List<MeshSnapshot> meshData,
        (Vector3 position, float arrivalMs)[] seeds,
        float defaultSpeed,
        float weldEps,
        float lengthScale,
        InfarctBlockInput? infarct,
        StreamlineOrientation orientation)
    {
        var allPositions = new List<Vector3>();
        var allIndices = new List<int>();
        var meshBase = new int[meshData.Count];
        for (int m = 0; m < meshData.Count; m++)
        {
            meshBase[m] = allPositions.Count;
            allPositions.AddRange(meshData[m].Positions);
            var idx = meshData[m].Indices;
            int b = meshBase[m];
            for (int i = 0; i < idx.Length; i++)
            {
                allIndices.Add(idx[i] + b);
            }
        }

        var mesh = SurfaceMesh.Weld(allPositions.ToArray(), allIndices.ToArray(), weldEps, out var rawToWelded);

        var eikSeeds = new List<EikonalSeed>(seeds.Length);
        foreach (var (position, arrivalMs) in seeds)
        {
            int v = mesh.NearestVertex(position);
            if (v < 0)
            {
                continue;
            }
            float gap = Vector3.Distance(position, mesh.Positions[v]);
            eikSeeds.Add(new EikonalSeed(v, arrivalMs + gap / defaultSpeed));
        }

        var options = new EikonalOptions { DefaultSpeed = defaultSpeed };

        // Infarct → conduction block: a welded vertex is dead scar if any of its (infarct-mesh) raw
        // vertices samples the necrosis mask above the threshold. Such vertices never activate, so the
        // wavefront must route around them.
        if (infarct is { } inf)
        {
            var blocked = new bool[mesh.VertexCount];
            bool any = false;
            int infarctMeshes = 0, sampled = 0, blockedCount = 0;
            float maxMask = 0f;
            for (int m = 0; m < meshData.Count; m++)
            {
                var snap = meshData[m];
                if (!snap.IsInfarct || snap.Uvs is not { } uvs)
                {
                    continue;
                }
                infarctMeshes++;
                int b = meshBase[m];
                for (int i = 0; i < uvs.Length; i++)
                {
                    sampled++;
                    float mv = inf.Mask.SampleMask(uvs[i].X, uvs[i].Y);
                    if (mv > maxMask) maxMask = mv;
                    if (mv * inf.Progress >= inf.Threshold)
                    {
                        blocked[rawToWelded[b + i]] = true;
                        blockedCount++;
                        any = true;
                    }
                }
            }
            Log($"infarct block: infarctMeshesWithUv={infarctMeshes} sampled={sampled} blocked={blockedCount} maxMask={maxMask:F3} progress={inf.Progress:F2}");
            if (any)
            {
                options.Blocked = blocked;
            }
        }

        float[] welded = new EikonalSolver(mesh).Solve(eikSeeds, options);

        var result = new List<float[]>(meshData.Count);
        for (int m = 0; m < meshData.Count; m++)
        {
            int count = meshData[m].Positions.Length;
            var times = new float[count];
            int b = meshBase[m];
            for (int i = 0; i < count; i++)
            {
                times[i] = welded[rawToWelded[b + i]];
            }
            result.Add(times);
        }

        // Streamline glyph orientation: along the wave's travel direction, or along the myocardial fibre
        // architecture (rule-based). Both are coloured by activation time downstream.
        var directions = orientation == StreamlineOrientation.Fiber
            ? ComputeFiberDirections(mesh)
            : mesh.ComputeVertexGradient(welded);
        var (linePositions, lineActivation) = BuildStreamlines(mesh, welded, directions, lengthScale);
        return new WavefrontSolution(result, linePositions, lineActivation);
    }

    /// <summary>
    /// Rule-based epicardial fibre field: solves a long-axis (apex↔base) Laplace field over the surface
    /// (base/apex ends pinned via the model's principal axis), takes its gradient as the local long-axis
    /// direction, then rotates that by a helix angle in the tangent plane — the classic simplification of
    /// myocardial fibre orientation, generated from geometry alone (no external DTI dataset). Vertices
    /// with no well-defined long-axis direction return a zero vector (their glyph is skipped).
    /// </summary>
    private static Vector3[] ComputeFiberDirections(SurfaceMesh mesh)
    {
        int n = mesh.VertexCount;
        if (n == 0)
        {
            return Array.Empty<Vector3>();
        }

        // Centroid + principal (long) axis by power iteration on the covariance operator.
        var centroid = Vector3.Zero;
        for (int i = 0; i < n; i++)
        {
            centroid += mesh.Positions[i];
        }
        centroid /= n;

        var variance = Vector3.Zero;
        for (int i = 0; i < n; i++)
        {
            var d = mesh.Positions[i] - centroid;
            variance += new Vector3(d.X * d.X, d.Y * d.Y, d.Z * d.Z);
        }
        var axis = variance.X >= variance.Y && variance.X >= variance.Z ? Vector3.UnitX
                 : variance.Y >= variance.Z ? Vector3.UnitY : Vector3.UnitZ;
        for (int it = 0; it < 40; it++)
        {
            var acc = Vector3.Zero;
            for (int i = 0; i < n; i++)
            {
                var d = mesh.Positions[i] - centroid;
                acc += d * Vector3.Dot(d, axis);
            }
            float len = acc.Length();
            if (len > 1e-9f)
            {
                axis = acc / len;
            }
        }

        // Pin the two extreme bands along the axis (base end = 0, apex end = 1), solve the Laplace field.
        float mn = float.MaxValue, mx = float.MinValue;
        var proj = new float[n];
        for (int i = 0; i < n; i++)
        {
            proj[i] = Vector3.Dot(mesh.Positions[i] - centroid, axis);
            if (proj[i] < mn) mn = proj[i];
            if (proj[i] > mx) mx = proj[i];
        }
        float range = MathF.Max(mx - mn, 1e-6f);
        float lo = mn + 0.08f * range, hi = mx - 0.08f * range;
        var mask = new bool[n];
        var vals = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (proj[i] <= lo) { mask[i] = true; vals[i] = 0f; }
            else if (proj[i] >= hi) { mask[i] = true; vals[i] = 1f; }
        }
        var phi = mesh.SolveLaplace(mask, vals, 300);

        var longAxis = mesh.ComputeVertexGradient(phi);
        var normals = mesh.ComputeVertexNormals();
        float theta = FiberHelixAngleDeg * MathF.PI / 180f;
        float cos = MathF.Cos(theta), sin = MathF.Sin(theta);
        var fiber = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var nrm = normals[i];
            var la = longAxis[i];
            la -= nrm * Vector3.Dot(la, nrm); // project into the tangent plane
            float l = la.Length();
            if (l < 1e-6f)
            {
                fiber[i] = Vector3.Zero;
                continue;
            }
            la /= l;
            // Rodrigues rotation of la about the unit normal by the helix angle (la ⟂ nrm).
            fiber[i] = la * cos + Vector3.Cross(nrm, la) * sin;
        }
        return fiber;
    }

    /// <summary>
    /// Builds streamline glyphs: at a subsample of reachable surface vertices, a short segment centred on
    /// the vertex, oriented along <paramref name="directions"/> (wave-travel or fibre) and lifted off the
    /// surface along the normal so it doesn't z-fight. Returns endpoint positions (pairs) and the
    /// per-endpoint activation time (both endpoints share the seed vertex's time) for colouring.
    /// </summary>
    private static (Vector3[] positions, float[] activation) BuildStreamlines(
        SurfaceMesh mesh, float[] activation, Vector3[] directions, float lengthScale)
    {
        const int MaxSegments = 6000;
        var normals = mesh.ComputeVertexNormals();
        float half = MathF.Max(lengthScale * 0.011f, 1e-4f);
        float lift = lengthScale * 0.003f;
        int vcount = mesh.VertexCount;
        int stride = Math.Max(1, vcount / MaxSegments);

        var positions = new List<Vector3>();
        var acts = new List<float>();
        for (int v = 0; v < vcount; v += stride)
        {
            float t = activation[v];
            if (!float.IsFinite(t))
            {
                continue; // blocked / unreachable tissue carries no wavefront
            }
            var dir = directions[v];
            float len = dir.Length();
            if (len < 1e-6f)
            {
                continue; // no well-defined direction here (wave source / fibre singularity)
            }
            dir /= len;
            var p = mesh.Positions[v] + normals[v] * lift;
            positions.Add(p - dir * half);
            positions.Add(p + dir * half);
            acts.Add(t);
            acts.Add(t);
        }
        return (positions.ToArray(), acts.ToArray());
    }

    private static string BuildWavefrontCacheKey(string? modelPath, float defaultSpeed, ConductionPath path, int infarctBucket, StreamlineOrientation orientation)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(modelPath ?? "?").Append('@')
          .Append(defaultSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture))
          .Append("#inf").Append(infarctBucket)
          .Append("#ori").Append((int)orientation);
        foreach (var n in path.Nodes)
        {
            var p = n.Position;
            sb.Append('|').Append(n.Key).Append(':').Append(n.ArrivalMs)
              .Append(':').Append(p.X).Append(',').Append(p.Y).Append(',').Append(p.Z);
        }
        return sb.ToString();
    }

    private static void StoreWavefrontCache(string key, WavefrontSolution value)
    {
        if (!_wavefrontCache.ContainsKey(key))
        {
            _wavefrontCacheOrder.Enqueue(key);
            while (_wavefrontCacheOrder.Count > WavefrontCacheCap && _wavefrontCacheOrder.TryDequeue(out var oldest))
            {
                _wavefrontCache.Remove(oldest);
            }
        }
        _wavefrontCache[key] = value;
    }

    /// <summary>
    /// Swaps every wavefront mesh to the flat vertex-colour material (remembering its original once) so the
    /// per-vertex activation colours show. Safe to call repeatedly — including when an off-thread solve
    /// finishes after the view was already toggled on.
    /// </summary>
    private void ApplyWavefrontMaterials()
    {
        if (_wavefrontMaterial is null)
        {
            return;
        }
        foreach (var mesh in _activationTimes.Keys)
        {
            if (!_preWavefrontMaterials.ContainsKey(mesh))
            {
                _preWavefrontMaterials[mesh] = mesh.Material;
            }
            mesh.Material = _wavefrontMaterial;
        }
    }

    private void ToggleWavefront()
    {
        if (_importedRoot is null) return;
        _wavefrontOn = !_wavefrontOn;
        if (_wavefrontMaterial == null)
        {
            _wavefrontMaterial = new PhongMaterialCore
            {
                DiffuseColor = new Hmx.Color4(1f, 1f, 1f, 1f),
                AmbientColor = new Hmx.Color4(0.2f, 0.2f, 0.2f, 1f),
                SpecularColor = new Hmx.Color4(0.1f, 0.1f, 0.1f, 1f),
                // Vertex-colour blending is opt-in and defaults to 0, so a PhongMaterial ignores
                // geom.Colors entirely. Full blend => the per-vertex wavefront colours actually render.
                VertexColorBlendingFactor = 1.0f,
            };
        }

        if (_wavefrontOn)
        {
            _preWavefrontMaterials.Clear();
            ApplyWavefrontMaterials();
            if (_wavefrontButton != null)
                _wavefrontButton.Content = GetString("Normal view", "Обычный вид");
            // If the infarct developed while the wavefront was off, the current map predates the scar —
            // re-solve so dead tissue blocks conduction (no-op when the bucket already matches).
            MaybeResolveWavefrontForInfarct();
        }
        else
        {
            foreach (var kvp in _preWavefrontMaterials)
            {
                if (kvp.Key.Geometry is HelixToolkit.SharpDX.MeshGeometry3D geom)
                {
                    geom.Colors = null;
                }
                kvp.Key.Material = kvp.Value;
            }
            if (_wavefrontButton != null)
                _wavefrontButton.Content = GetString("Wavefront view", "Волны деполяризации");
        }
    }

    private static Hmx.Color4 LerpColor(Hmx.Color4 a, Hmx.Color4 b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Hmx.Color4(
            a.Red + (b.Red - a.Red) * t,
            a.Green + (b.Green - a.Green) * t,
            a.Blue + (b.Blue - a.Blue) * t,
            1f
        );
    }

    /// <summary>Selectable depolarisation colour ramps for the wavefront view.</summary>
    private enum WavefrontScheme { Classic, Thermal, Viridis, Ice, Fire }

    /// <summary>How the streamline glyphs are oriented.</summary>
    private enum StreamlineOrientation { Propagation, Fiber }

    private static (string en, string ru) OrientationName(StreamlineOrientation o) => o switch
    {
        StreamlineOrientation.Propagation => ("By wave", "По волне"),
        StreamlineOrientation.Fiber => ("By fibres", "По волокнам"),
        _ => (o.ToString(), o.ToString()),
    };

    private static (string en, string ru) SchemeName(WavefrontScheme s) => s switch
    {
        WavefrontScheme.Classic => ("Classic (blue→red)", "Классическая"),
        WavefrontScheme.Thermal => ("Thermal", "Тепловая"),
        WavefrontScheme.Viridis => ("Viridis", "Viridis"),
        WavefrontScheme.Ice => ("Ice", "Ледяная"),
        WavefrontScheme.Fire => ("Fire", "Огненная"),
        _ => (s.ToString(), s.ToString()),
    };

    private static Hmx.Color4 Col(float r, float g, float b) => new(r, g, b, 1f);

    /// <summary>
    /// Colour stops (ascending position 0..1) for each scheme. Position 0 is resting tissue, 1 is peak
    /// depolarisation. Classic reproduces the original blue→red look exactly.
    /// </summary>
    private static (float pos, Hmx.Color4 col)[] SchemeStops(WavefrontScheme s) => s switch
    {
        WavefrontScheme.Classic => new[] { (0f, Col(0f, 0f, 1f)), (1f, Col(1f, 0f, 0f)) },
        WavefrontScheme.Thermal => new[]
        {
            (0f, Col(0.02f, 0.05f, 0.30f)), (0.35f, Col(0f, 0.65f, 0.85f)),
            (0.65f, Col(0.20f, 0.85f, 0.25f)), (1f, Col(1f, 0.95f, 0.20f)),
        },
        WavefrontScheme.Viridis => new[]
        {
            (0f, Col(0.27f, 0f, 0.33f)), (0.35f, Col(0.13f, 0.44f, 0.55f)),
            (0.70f, Col(0.20f, 0.71f, 0.47f)), (1f, Col(0.99f, 0.91f, 0.14f)),
        },
        WavefrontScheme.Ice => new[] { (0f, Col(0.01f, 0.02f, 0.12f)), (1f, Col(0.55f, 0.88f, 1f)) },
        WavefrontScheme.Fire => new[]
        {
            (0f, Col(0.08f, 0f, 0f)), (0.5f, Col(1f, 0.25f, 0f)), (1f, Col(1f, 1f, 0.65f)),
        },
        _ => new[] { (0f, Col(0f, 0f, 1f)), (1f, Col(1f, 0f, 0f)) },
    };

    /// <summary>Maps a normalised activation intensity 0..1 to a colour via the scheme's ramp.</summary>
    private static Hmx.Color4 SampleScheme(WavefrontScheme scheme, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var stops = SchemeStops(scheme);
        for (int i = 1; i < stops.Length; i++)
        {
            if (t <= stops[i].pos)
            {
                var a = stops[i - 1];
                var b = stops[i];
                float span = b.pos - a.pos;
                return LerpColor(a.col, b.col, span > 1e-6f ? (t - a.pos) / span : 0f);
            }
        }
        return stops[^1].col;
    }

    /// <summary>
    /// Action-potential "intensity" 0..1 at a vertex, given ms since it depolarised: a fast upstroke to
    /// 1, a plateau, then repolarisation back to 0 (resting). Drives the colour-ramp lookup.
    /// </summary>
    private static float WavefrontIntensity(float tSince)
    {
        if (tSince < 0f) return 0f;
        if (tSince < ApUpstrokeMs) return tSince / ApUpstrokeMs;
        if (tSince < ApUpstrokeMs + ApPlateauMs) return 1f;
        if (tSince < ApUpstrokeMs + ApPlateauMs + ApRepolMs)
        {
            return 1f - (tSince - ApUpstrokeMs - ApPlateauMs) / ApRepolMs;
        }
        return 0f;
    }

    private void AdvanceWavefront(float cycleTimeMs, float cycleLength)
    {
        var scheme = _wavefrontScheme;
        var resting = SampleScheme(scheme, 0f);

        foreach (var kvp in _activationTimes)
        {
            var mesh = kvp.Key;
            var times = kvp.Value;
            if (mesh.Geometry is not HelixToolkit.SharpDX.MeshGeometry3D geom) continue;

#pragma warning disable CS8600, CS8601, CS8602
            if (geom.Colors == null || geom.Colors.Count != times.Length)
            {
                var colorsType = typeof(HelixToolkit.SharpDX.MeshGeometry3D).GetProperty("Colors")?.PropertyType;
                if (colorsType != null && Activator.CreateInstance(colorsType) is { } newColors)
                {
                    dynamic dynamicNewColors = newColors;
                    for (int i = 0; i < times.Length; i++) dynamicNewColors.Add(resting);
                    geom.Colors = dynamicNewColors;
                }
            }

            dynamic? colors = geom.Colors;
            if (colors != null)
            {
                for (int i = 0; i < times.Length; i++)
                {
                    float tSince = cycleTimeMs - times[i];
                    if (tSince < 0) tSince += cycleLength;
                    colors[i] = SampleScheme(scheme, WavefrontIntensity(tSince));
                }
                geom.UpdateColors();
            }
#pragma warning restore CS8600, CS8601, CS8602
        }
    }

    /// <summary>Toggles the propagation-streamline overlay (independent of the solid wavefront view).</summary>
    private void ToggleStreamlines()
    {
        _streamlinesOn = !_streamlinesOn;
        if (_streamlineModel is not null)
        {
            _streamlineModel.IsRendering = _streamlinesOn && _streamlineModel.Geometry is not null;
        }
        if (_streamlineButton is not null)
        {
            _streamlineButton.Content = _streamlinesOn
                ? GetString("Hide streamlines", "Скрыть линии")
                : GetString("Streamlines", "Линии волны");
        }
    }

    /// <summary>Installs freshly-solved streamline glyphs into the line model (UI thread).</summary>
    private void ApplyStreamlineGeometry(Vector3[] positions, float[] activation)
    {
        _streamlineActivation = activation;
        if (_streamlineModel is null)
        {
            return;
        }
        if (positions.Length == 0)
        {
            _streamlineModel.Geometry = null;
            _streamlineModel.IsRendering = false;
            return;
        }
        var pos = new Vector3Collection(positions.Length);
        var idx = new IntCollection(positions.Length);
        var cols = new Color4Collection(positions.Length);
        var resting = SampleScheme(_wavefrontScheme, 0f);
        for (int i = 0; i < positions.Length; i++)
        {
            pos.Add(positions[i]);
            idx.Add(i); // consecutive index pairs (0,1)(2,3)… are the line segments
            cols.Add(resting);
        }
        _streamlineModel.Geometry = new LineGeometry3D { Positions = pos, Indices = idx, Colors = cols };
        _streamlineModel.IsRendering = _streamlinesOn;
    }

    /// <summary>Recolours the streamline glyphs for the current cycle time (same ramp as the mesh).</summary>
    private void AdvanceStreamlines(float cycleTimeMs, float cycleLength)
    {
        if (_streamlineModel?.Geometry is not LineGeometry3D geom || geom.Colors is null)
        {
            return;
        }
        var scheme = _wavefrontScheme;
        int n = Math.Min(_streamlineActivation.Length, geom.Colors.Count);
        for (int i = 0; i < n; i++)
        {
            float tSince = cycleTimeMs - _streamlineActivation[i];
            if (tSince < 0) tSince += cycleLength;
            geom.Colors[i] = SampleScheme(scheme, WavefrontIntensity(tSince));
        }
        geom.UpdateColors();
    }

    private void ToggleTransparency()
    {
        _transparent = !_transparent;
        ApplyTransparency(_transparent);
        _xrayButton.Content = _transparent
            ? GetString("Solid view", "Непрозрачно")
            : GetString("X-ray view", "Просвечивание");
    }

    /// <summary>
    /// Fades the imported myocardium to translucent (or restores it) so the internal conduction
    /// pathway shows through. Handles both Phong (DiffuseColor) and PBR (AlbedoColor) materials; the
    /// original colour is cached per mesh so the restore is exact.
    /// </summary>
    private void ApplyTransparency(bool on)
    {
        if (_importedRoot is null)
        {
            return;
        }
        const float alpha = 0.28f;
        TraverseMeshes(_importedRoot, mesh =>
        {
            if (on)
            {
                if (mesh.Material is PhongMaterialCore phong)
                {
                    if (!_originalDiffuse.ContainsKey(mesh))
                    {
                        _originalDiffuse[mesh] = phong.DiffuseColor;
                    }
                    var c = _originalDiffuse[mesh];
                    phong.DiffuseColor = new Hmx.Color4(c.Red, c.Green, c.Blue, alpha);
                    mesh.IsTransparent = true;
                }
                else if (mesh.Material is PBRMaterialCore pbr)
                {
                    if (!_originalDiffuse.ContainsKey(mesh))
                    {
                        _originalDiffuse[mesh] = pbr.AlbedoColor;
                    }
                    var c = _originalDiffuse[mesh];
                    pbr.AlbedoColor = new Hmx.Color4(c.Red, c.Green, c.Blue, alpha);
                    mesh.IsTransparent = true;
                }
            }
            else
            {
                if (_originalDiffuse.TryGetValue(mesh, out var orig))
                {
                    if (mesh.Material is PhongMaterialCore phong)
                    {
                        phong.DiffuseColor = orig;
                    }
                    else if (mesh.Material is PBRMaterialCore pbr)
                    {
                        pbr.AlbedoColor = orig;
                    }
                }
                mesh.IsTransparent = false;
            }
        });
    }

    /// <summary>Mesh-name fragments (lower-case) that mark the non-heart scaffolding in the scene model.</summary>
    private static readonly string[] NonHeartMeshTokens =
        { "silhouette", "human", "ecg", "lead", "axes", "text" };

    /// <summary>
    /// Hides the non-heart meshes (human silhouette + ECG lead system/axes/text) so the dialog shows
    /// the heart, and returns the combined world-space bounds of the remaining heart + coronary meshes
    /// for camera framing. Returns <c>null</c> when the model has no such scaffolding (e.g. a plain
    /// heart model), so whole-scene framing is left untouched.
    /// </summary>
    private (Vector3 centroid, float maxDim, Hmx.BoundingBox bounds)? IsolateHeart(SceneNode root)
    {
        _scaffoldMeshes.Clear();
        bool haveBounds = false;
        Vector3 min = default, max = default;
        TraverseMeshes(root, mesh =>
        {
            var name = (mesh.Name ?? string.Empty).ToLowerInvariant();
            if (NonHeartMeshTokens.Any(token => name.Contains(token)))
            {
                mesh.Visible = false;
                _scaffoldMeshes.Add(mesh); // remembered so the leads-scheme toggle can show them again
                return;
            }
            if (mesh.HasBound)
            {
                var b = mesh.BoundsWithTransform;
                min = haveBounds ? Vector3.Min(min, b.Minimum) : b.Minimum;
                max = haveBounds ? Vector3.Max(max, b.Maximum) : b.Maximum;
                haveBounds = true;
            }
        });
        if (_scaffoldMeshes.Count == 0 || !haveBounds)
        {
            return null;
        }
        var size = max - min;
        float maxDim = Math.Max(Math.Max(size.X, size.Y), size.Z);
        var centroid = (min + max) * 0.5f;
        return (centroid, maxDim, new Hmx.BoundingBox(min, max));
    }

    /// <summary>Resets the leads-scheme toggle for a freshly loaded model; enables the button only when
    /// the model actually carries the silhouette/ECG scaffolding.</summary>
    private void InitLeadsScheme()
    {
        _leadsSchemeOn = false;
        if (_leadsSchemeButton is not null)
        {
            _leadsSchemeButton.IsEnabled = _scaffoldMeshes.Count > 0;
            _leadsSchemeButton.Content = AppStrings.Monitor3DLeadScheme;
        }
    }

    /// <summary>
    /// Shows/hides the ECG leads scheme — the human silhouette + ECG lead system/axes/text that
    /// <see cref="IsolateHeart"/> hides for the default heart-only view — and reframes between the whole
    /// scene (scheme on) and the heart alone (scheme off). No-op for a plain heart model.
    /// </summary>
    private void ToggleLeadsScheme()
    {
        if (_scaffoldMeshes.Count == 0)
        {
            return;
        }
        _leadsSchemeOn = !_leadsSchemeOn;
        foreach (var mesh in _scaffoldMeshes)
        {
            mesh.Visible = _leadsSchemeOn;
        }
        // X-ray sets alpha per material; re-apply so any newly shown meshes match the current state.
        if (_transparent)
        {
            ApplyTransparency(true);
        }
        if (_leadsSchemeOn)
        {
            FrameCamera(_sceneCentroid, _sceneFrameDim);
            _leadsSchemeButton.Content = GetString("Hide leads scheme", "Скрыть схему");
        }
        else
        {
            FrameCamera(_heartCentroid, _modelMaxDim);
            _leadsSchemeButton.Content = AppStrings.Monitor3DLeadScheme;
        }
    }

    private static void TraverseMeshes(SceneNode node, Action<MeshNode> action)
    {
        if (node is MeshNode mesh)
        {
            action(mesh);
        }
        if (node.Items is null)
        {
            return;
        }
        foreach (var child in node.Items)
        {
            TraverseMeshes(child, action);
        }
    }

    // ---- Infarct visualisation: controls, setup, blend/apply, animation ----

    /// <summary>
    /// Left-column group: the infarct progress slider and a "develop" animation button. Hidden until
    /// a model with the healthy/infarct/mask sidecar textures is loaded (see <see cref="SetupInfarct"/>).
    /// </summary>
    private FrameworkElement BuildInfarctControls()
    {
        var header = new TextBlock
        {
            Text = GetString("Infarct (necrosis)", "Инфаркт (некроз)"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 2),
            TextWrapping = TextWrapping.Wrap,
        };

        _infarctLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = InfoGray,
            Text = GetString("Healthy myocardium", "Здоровый миокард"),
        };

        _infarctPlayButton = FunctionButton(GetString("▶ Develop infarct", "▶ Развитие инфаркта"));
        _infarctPlayButton.Click += (_, _) => ToggleInfarctPlay();

        _infarctSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            StepFrequency = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _infarctSlider.ValueChanged += (_, e) => OnInfarctSliderChanged(e.NewValue / 100.0);

        _infarctControls = new StackPanel
        {
            Spacing = 8,
            Visibility = Visibility.Collapsed,
            Children = { header, _infarctLabel, _infarctPlayButton, _infarctSlider },
        };
        return _infarctControls;
    }

    /// <summary>
    /// After a model loads: find the textured heart meshes (PBR materials that already carry an
    /// albedo map), enable shader-derived tangents so the normal map lights correctly, cache their
    /// original albedo, then load the sidecar infarct textures if the model ships them.
    /// </summary>
    private void SetupInfarct(SceneNode root, string modelPath)
    {
        StopInfarctPlay();
        _infarctMaterials.Clear();
        _infarctMeshes.Clear();
        _originalAlbedo.Clear();
        _infarctSet = null;
        _infarctProgress = 0f;
        _appliedInfarctProgress = -1f;
        _lastInfarctBuildProgress = -1f;

        TraverseMeshes(root, mesh =>
        {
            var mat = mesh.Material;
            var map = GetDiffuseOrAlbedo(mat);
            if (mat is null || map is null)
            {
                return; // no colour texture ⇒ not a heart-skin mesh (e.g. ECG leads); skip
            }
            _infarctMeshes.Add(mesh); // by node identity — survives the wavefront material swap
            if (!_infarctMaterials.Contains(mat))
            {
                _infarctMaterials.Add(mat);
                _originalAlbedo[mat] = map;
            }
            // The imported mesh has no baked tangents; let the shader derive a tangent basis, and make
            // sure the embedded normal map (surface relief) is actually lit.
            if (mat is PhongMaterialCore phong)
            {
                phong.EnableAutoTangent = true;
                phong.RenderNormalMap = true;
            }
            else if (mat is PBRMaterialCore pbr)
            {
                pbr.EnableAutoTangent = true;
            }
        });

        SetSliderSuppressed(0);
        UpdateInfarctLabel();

        var paths = InfarctTextureSet.Resolve(modelPath);
        if (paths is null || _infarctMaterials.Count == 0)
        {
            _infarctControls.Visibility = Visibility.Collapsed;
            return;
        }
        _ = LoadInfarctTexturesAsync(paths.Value.healthy, paths.Value.infarct, paths.Value.mask);
    }

    private async Task LoadInfarctTexturesAsync(string healthy, string infarct, string mask)
    {
        try
        {
            var set = await InfarctTextureSet.LoadAsync(healthy, infarct, mask);
            if (set is null)
            {
                Log("Infarct textures missing or mismatched; hiding the infarct control.");
                _infarctControls.Visibility = Visibility.Collapsed;
                return;
            }
            _infarctSet = set;
            _infarctControls.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log($"Infarct texture load failed: {ex.Message}");
            _infarctControls.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Manual scrub: cancels any running animation and applies the chosen progress.</summary>
    private void OnInfarctSliderChanged(double value01)
    {
        if (_suppressSlider)
        {
            return; // programmatic move from the animation — don't re-enter
        }
        StopInfarctPlay();
        RequestInfarctProgress((float)value01);
    }

    private void RequestInfarctProgress(float progress)
    {
        _infarctProgress = Math.Clamp(progress, 0f, 1f);
        UpdateInfarctLabel();
        ApplyInfarctProgress(_infarctProgress);
        MaybeResolveWavefrontForInfarct(); // dead scar becomes non-conducting → re-route the wave
    }

    /// <summary>
    /// Pushes the blended albedo for <paramref name="progress"/> to the heart materials. The blend
    /// runs off the UI thread; rapid changes are coalesced so only the latest target is built.
    /// </summary>
    private void ApplyInfarctProgress(float progress)
    {
        if (_infarctSet is null || _infarctMaterials.Count == 0)
        {
            return;
        }

        // At (near) zero, restore the original imported albedo — pixel-identical to the authored heart
        // and free of a needless blend/upload.
        if (progress <= 0.001f)
        {
            _pendingInfarctProgress = null;
            foreach (var mat in _infarctMaterials)
            {
                if (_originalAlbedo.TryGetValue(mat, out var orig))
                {
                    SetDiffuseOrAlbedo(mat, orig);
                }
            }
            _appliedInfarctProgress = 0f;
            return;
        }

        if (_infarctBuilding)
        {
            _pendingInfarctProgress = progress; // coalesce: remember only the latest
            return;
        }
        _infarctBuilding = true;
        _ = BuildAndApplyAsync(progress);
    }

    private async Task BuildAndApplyAsync(float progress)
    {
        try
        {
            var set = _infarctSet;
            if (set is null)
            {
                return;
            }
            // Blend on the thread pool; the await resumes on the UI thread (WinUI sync context) where
            // the material/GPU assignment must happen.
            var bgra = await Task.Run(() => set.Blend(progress));
            if (_infarctSet is null)
            {
                return; // model changed/closed while building
            }
            var texture = set.Wrap(bgra);
            foreach (var mat in _infarctMaterials)
            {
                SetDiffuseOrAlbedo(mat, texture);
            }
            _appliedInfarctProgress = progress;
        }
        catch (Exception ex)
        {
            Log($"Infarct blend/apply failed: {ex.Message}");
        }
        finally
        {
            _infarctBuilding = false;
            if (_pendingInfarctProgress is { } next)
            {
                _pendingInfarctProgress = null;
                if (next <= 0.001f || Math.Abs(next - _appliedInfarctProgress) > 0.004f)
                {
                    ApplyInfarctProgress(next);
                }
            }
        }
    }

    private void ToggleInfarctPlay()
    {
        if (_infarctSet is null)
        {
            return;
        }
        if (_infarctPlaying)
        {
            StopInfarctPlay();
            return;
        }
        // Replay from the start if we're already at full necrosis.
        if (_infarctProgress >= 0.999f)
        {
            _infarctProgress = 0f;
            SetSliderSuppressed(0);
            UpdateInfarctLabel();
            ApplyInfarctProgress(0f);
        }
        _infarctPlaying = true;
        _infarctStartProgress = _infarctProgress;
        _lastInfarctBuildProgress = _infarctProgress;
        _infarctClock.Restart();
        _infarctPlayButton.Content = GetString("⏸ Pause", "⏸ Пауза");
    }

    private void StopInfarctPlay()
    {
        if (!_infarctPlaying)
        {
            return;
        }
        _infarctPlaying = false;
        _infarctClock.Stop();
        if (_infarctPlayButton is not null)
        {
            _infarctPlayButton.Content = GetString("▶ Develop infarct", "▶ Развитие инфаркта");
        }
    }

    /// <summary>Drives the progress from the stopwatch each frame while playing; throttles GPU rebuilds.</summary>
    private void AdvanceInfarct()
    {
        if (!_infarctPlaying || _infarctSet is null)
        {
            return;
        }
        float elapsed = (float)_infarctClock.Elapsed.TotalSeconds;
        float p = Math.Min(1f, _infarctStartProgress + elapsed / InfarctDurationSeconds);
        _infarctProgress = p;
        SetSliderSuppressed(p * 100.0);
        UpdateInfarctLabel();
        MaybeResolveWavefrontForInfarct(); // guarded to re-solve only when the necrosis bucket changes

        // The thumb + label track the animation every frame (above). Rebuild the blended texture on a
        // finer cadence (~2% ⇒ ~8 uploads/s over the 6 s run) so the visible necrosis grows in step with
        // the gliding thumb, while still avoiding a GPU upload on literally every frame.
        if (p >= 1f || Math.Abs(p - _lastInfarctBuildProgress) >= 0.02f)
        {
            _lastInfarctBuildProgress = p;
            ApplyInfarctProgress(p);
        }
        if (p >= 1f)
        {
            StopInfarctPlay();
        }
    }

    private void SetSliderSuppressed(double value)
    {
        if (_infarctSlider is null)
        {
            return;
        }
        _suppressSlider = true;
        _infarctSlider.Value = value;
        _suppressSlider = false;
    }

    private void UpdateInfarctLabel()
    {
        if (_infarctLabel is null)
        {
            return;
        }
        int pct = (int)Math.Round(_infarctProgress * 100);
        _infarctLabel.Text = pct <= 0
            ? GetString("Healthy myocardium", "Здоровый миокард")
            : pct >= 100
                ? GetString("Full infarct", "Полный инфаркт")
                : GetString($"Infarct: {pct}%", $"Инфаркт: {pct}%");
    }

    /// <summary>The colour texture of a material, whichever shading model it uses (Phong or PBR).</summary>
    private static TextureModel? GetDiffuseOrAlbedo(MaterialCore? mat) => mat switch
    {
        PhongMaterialCore phong => phong.DiffuseMap,
        PBRMaterialCore pbr => pbr.AlbedoMap,
        _ => null,
    };

    /// <summary>Sets the colour texture of a material, whichever shading model it uses.</summary>
    private static void SetDiffuseOrAlbedo(MaterialCore mat, TextureModel? map)
    {
        switch (mat)
        {
            case PhongMaterialCore phong:
                phong.DiffuseMap = map;
                break;
            case PBRMaterialCore pbr:
                pbr.AlbedoMap = map;
                break;
        }
    }

    /// <summary>Enters/leaves pathway-authoring mode; on exit, saves the authored path to a sidecar.</summary>
    private void ToggleConductionEdit()
    {
        _conductionEditMode = !_conductionEditMode;
        if (_conductionEditMode)
        {
            StopConduction();
            _conductionPath = new ConductionPath(); // author from scratch, in anatomical order
            RebuildConductionGeometry();
            _conductionEditButton.Content = GetString("Done editing", "Готово");
            UpdateEditHint();
        }
        else
        {
            _conductionEditButton.Content = GetString("Edit pathway", "Ред. путь");
            if (_editHintHost is not null)
            {
                _editHintHost.Visibility = Visibility.Collapsed;
            }
            if (_conductionPath is { Nodes.Count: > 0 } && !string.IsNullOrEmpty(_currentModelPath))
            {
                _conductionPath.Save(_currentModelPath);
            }
            // Recompute the wavefront activation map so it reflects the freshly authored pathway.
            PrecomputeWavefront();
        }
    }

    /// <summary>Appends the next anatomical node at a clicked surface point while authoring.</summary>
    private void PlaceNextConductionNode(Vector3 anchor)
    {
        _conductionPath ??= new ConductionPath();
        int i = _conductionPath.Nodes.Count;
        if (i >= ConductionPath.Template.Count)
        {
            return;
        }
        var stage = ConductionPath.Template[i];
        _conductionPath.Nodes.Add(new ConductionNode
        {
            Key = stage.Key,
            LabelEn = stage.En,
            LabelRu = stage.Ru,
            ArrivalMs = stage.ArrivalMs,
            Anchor = new[] { anchor.X, anchor.Y, anchor.Z },
        });
        RebuildConductionGeometry();
        UpdateEditHint();
    }

    private void UpdateEditHint()
    {
        if (_editHintHost is null || _editHint is null)
        {
            return;
        }
        int i = _conductionPath?.Nodes.Count ?? 0;
        if (_conductionEditMode && i < ConductionPath.Template.Count)
        {
            var stage = ConductionPath.Template[i];
            _editHint.Text = GetString(
                $"Click to place: {stage.En}  ({i + 1}/{ConductionPath.Template.Count})",
                $"Кликните, чтобы поставить: {stage.Ru}  ({i + 1}/{ConductionPath.Template.Count})");
            _editHintHost.Visibility = Visibility.Visible;
        }
        else if (_conductionEditMode)
        {
            _editHint.Text = GetString(
                "Pathway complete — click Done editing", "Путь готов — нажмите «Готово»");
            _editHintHost.Visibility = Visibility.Visible;
        }
        else
        {
            _editHintHost.Visibility = Visibility.Collapsed;
        }
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
