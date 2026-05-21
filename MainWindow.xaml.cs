using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using Markdig;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WebViewCreationProperties = Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace MarkDNext;

public partial class MainWindow : Window
{
    private const string DocumentHost = "mdv-document.local";
    private const string AssetHost = "mdv-assets.local";
    private const int ListIndentSize = 4;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> EmbeddedAssetNames = new(BuildEmbeddedAssetMap);

    private readonly MarkdownPipeline _markdownPipeline;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _previewScrollTimer;
    private readonly DispatcherTimer _watchTimer;
    private readonly string? _initialFilePath;
    private readonly string _assetFolder;

    private CompletionWindow? _completionWindow;
    private FileSystemWatcher? _watcher;
    private string? _currentFilePath;
    private DateTime _lastDiskWriteUtc;
    private bool _isDirty;
    private bool _isLoadingDocument;
    private bool _isRenderingPreview;
    private bool _renderAgainRequested;
    private bool _isRenderingWysiwyg;
    private bool _wysiwygRenderAgainRequested;
    private bool _isUpdatingFromWysiwyg;
    private bool _previewReady;
    private bool _wysiwygReady;
    private bool _previewShellReady;
    private bool _wysiwygShellReady;
    private bool _previewFailed;
    private bool _wysiwygMode;
    private bool _automaticCompletionEnabled;
    private ViewMode _viewMode = ViewMode.Both;
    private ViewMode _viewModeBeforeWysiwyg = ViewMode.Both;
    private WindowBackdropKind _windowBackdrop = WindowBackdropKind.Flat;
    private string _editorFontFamily = "Consolas";
    private double _editorFontSize = 14;
    private string _contentFontFamily = "Segoe UI";
    private double _contentFontSize = 16;
    private ColorProfile _colorProfile = ColorProfile.Default;

    public MainWindow()
        : this(null)
    {
    }

    public MainWindow(string? filePath)
    {
        _initialFilePath = filePath;
        _assetFolder = Path.Combine(AppContext.BaseDirectory, "Assets");
        _markdownPipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _renderTimer.Tick += RenderTimer_Tick;

        _previewScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _previewScrollTimer.Tick += PreviewScrollTimer_Tick;

        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _watchTimer.Tick += WatchTimer_Tick;

        InitializeComponent();
        ConfigureEditor();
        ApplyAppearance();
        Loaded += MainWindow_Loaded;
    }

    private void ConfigureEditor()
    {
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            UpdatePosition();
            SchedulePreviewScrollSync();
        };
        Editor.TextArea.TextEntered += Editor_TextArea_TextEntered;
        Editor.TextArea.PreviewKeyDown += Editor_TextArea_PreviewKeyDown;
        Editor.TextArea.TextView.LineTransformers.Add(new MarkdownColorizer());
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewsAsync();

        if (!string.IsNullOrWhiteSpace(_initialFilePath) && File.Exists(_initialFilePath))
        {
            LoadFile(_initialFilePath);
        }
        else
        {
            NewDocument();
        }
    }

    private async Task InitializeWebViewsAsync()
    {
        ConfigureWebViewUserDataFolder(Preview);
        ConfigureWebViewUserDataFolder(Wysiwyg);

        try
        {
            await Preview.EnsureCoreWebView2Async();
            Preview.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Preview.CoreWebView2.NavigationStarting += Preview_NavigationStarting;
            Preview.CoreWebView2.WebMessageReceived += Preview_WebMessageReceived;
            ConfigureDocumentResourceHandler(Preview.CoreWebView2);
            MapAssetFolder(Preview.CoreWebView2);
            _previewReady = true;
        }
        catch (Exception ex)
        {
            _previewFailed = true;
            StatusText.Text = "Preview unavailable: install Microsoft Edge WebView2 Runtime.";
            MessageBox.Show(
                this,
                "The Markdown preview needs Microsoft Edge WebView2 Runtime.\n\n" + ex.Message,
                "MarkDNext",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        try
        {
            await Wysiwyg.EnsureCoreWebView2Async();
            Wysiwyg.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Wysiwyg.CoreWebView2.NavigationStarting += Preview_NavigationStarting;
            Wysiwyg.CoreWebView2.WebMessageReceived += Wysiwyg_WebMessageReceived;
            ConfigureDocumentResourceHandler(Wysiwyg.CoreWebView2);
            MapAssetFolder(Wysiwyg.CoreWebView2);
            _wysiwygReady = true;
        }
        catch (Exception ex)
        {
            _wysiwygReady = false;
            Debug.WriteLine(ex);
        }
    }

    private static void ConfigureWebViewUserDataFolder(WpfWebView2 webView)
    {
        webView.CreationProperties ??= new WebViewCreationProperties();
        webView.CreationProperties.UserDataFolder = GetWebViewUserDataFolder();
    }

    private static string GetWebViewUserDataFolder()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        return Path.Combine(localAppData, "MarkDNext", "WebView2");
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        SetWindowMaterial(_windowBackdrop);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmSaveChanges())
        {
            NewDocument();
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmSaveChanges())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Markdown files (*.md;*.markdown;*.mdown;*.mkd;*.txt)|*.md;*.markdown;*.mdown;*.mkd;*.txt|All files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            LoadFile(dialog.FileName);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await SyncWysiwygEditorAsync();
        SaveDocument();
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        await SyncWysiwygEditorAsync();
        SaveDocumentAs();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilePath is null || !File.Exists(_currentFilePath))
        {
            StatusText.Text = "There is no file to reload.";
            return;
        }

        if (_isDirty)
        {
            var result = MessageBox.Show(
                this,
                "Discard unsaved changes and reload the file from disk?",
                "Reload From Disk",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        ReloadFromDisk(showStatus: true);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_wysiwygMode)
        {
            await UndoWysiwygAsync();
            return;
        }

        if (Editor.CanUndo)
        {
            Editor.Undo();
        }
    }

    private async void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_wysiwygMode)
        {
            await RedoWysiwygAsync();
            return;
        }

        if (Editor.CanRedo)
        {
            Editor.Redo();
        }
    }

    private void Cut_Click(object sender, RoutedEventArgs e)
    {
        ExecuteEditorCommand(ApplicationCommands.Cut);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        ExecuteEditorCommand(ApplicationCommands.Copy);
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetClipboardImageReference(out var source, out var alt))
        {
            await InsertImageReferenceAsync(source, alt);
            return;
        }

        ExecuteEditorCommand(ApplicationCommands.Paste);
    }

    private void ExecuteEditorCommand(RoutedUICommand command)
    {
        var target = Editor.TextArea;
        if (command.CanExecute(null, target))
        {
            command.Execute(null, target);
        }
    }

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        ShowFindBar();
    }

    private async void FindNext_Click(object sender, RoutedEventArgs e)
    {
        await FindAsync(backwards: false);
    }

    private async void FindPrevious_Click(object sender, RoutedEventArgs e)
    {
        await FindAsync(backwards: true);
    }

    private async void InsertImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.svg)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.svg|All files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            await InsertImageAsync(dialog.FileName);
        }
    }

    private void CloseFind_Click(object sender, RoutedEventArgs e)
    {
        HideFindBar();
    }

    private void AutoCompletion_Click(object sender, RoutedEventArgs e)
    {
        _automaticCompletionEnabled = AutoCompletionMenuItem.IsChecked;
        StatusText.Text = _automaticCompletionEnabled
            ? "Automatic completion enabled"
            : "Automatic completion disabled.";
    }

    private void ViewBoth_Click(object sender, RoutedEventArgs e)
    {
        SetViewMode(ViewMode.Both);
    }

    private void ViewEditor_Click(object sender, RoutedEventArgs e)
    {
        SetViewMode(ViewMode.EditorOnly);
    }

    private void ViewPreview_Click(object sender, RoutedEventArgs e)
    {
        SetViewMode(ViewMode.PreviewOnly);
    }

    private async void SourceMode_Click(object sender, RoutedEventArgs e)
    {
        await SetWysiwygModeAsync(false);
    }

    private async void WysiwygMode_Click(object sender, RoutedEventArgs e)
    {
        await SetWysiwygModeAsync(true);
    }

    private void WordWrap_Click(object sender, RoutedEventArgs e)
    {
        Editor.WordWrap = WordWrapMenuItem.IsChecked;
    }

    private void Font_Click(object sender, RoutedEventArgs e)
    {
        var familyBox = new TextBox
        {
            Text = _editorFontFamily,
            MinWidth = 260,
            Margin = new Thickness(0, 4, 0, 10)
        };
        var sizeBox = new TextBox
        {
            Text = _editorFontSize.ToString("0.#", CultureInfo.InvariantCulture),
            MinWidth = 120,
            Margin = new Thickness(0, 4, 0, 14)
        };
        var okButton = new Button { Content = "OK", MinWidth = 72, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 72, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Font family" });
        panel.Children.Add(familyBox);
        panel.Children.Add(new TextBlock { Text = "Font size" });
        panel.Children.Add(sizeBox);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Font",
            Owner = this,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        okButton.Click += (_, _) => dialog.DialogResult = true;

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!double.TryParse(sizeBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var size)
            && !double.TryParse(sizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out size))
        {
            size = _editorFontSize;
        }

        _editorFontFamily = string.IsNullOrWhiteSpace(familyBox.Text) ? "Consolas" : familyBox.Text.Trim();
        _contentFontFamily = _editorFontFamily;
        _editorFontSize = Math.Clamp(size, 8, 48);
        _contentFontSize = Math.Clamp(size + 2, 10, 54);
        ApplyAppearance();
        RefreshRenderedShells();
        StatusText.Text = $"Font changed to {_editorFontFamily}";
    }

    private void IncreaseFont_Click(object sender, RoutedEventArgs e)
    {
        AdjustFontSize(1);
    }

    private void DecreaseFont_Click(object sender, RoutedEventArgs e)
    {
        AdjustFontSize(-1);
    }

    private void ResetFont_Click(object sender, RoutedEventArgs e)
    {
        _editorFontFamily = "Consolas";
        _contentFontFamily = "Segoe UI";
        _editorFontSize = 14;
        _contentFontSize = 16;
        ApplyAppearance();
        RefreshRenderedShells();
        StatusText.Text = "Font reset.";
    }

    private void LoadColorProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "MarkDNext color profile (*.json)|*.json|All files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(dialog.FileName, Encoding.UTF8);
            var profile = JsonSerializer.Deserialize<ColorProfile>(json, JsonOptions);
            if (profile is null)
            {
                throw new InvalidDataException("The selected file is not a color profile.");
            }

            _colorProfile = profile.Normalized();
            ApplyAppearance();
            RefreshRenderedShells();
            StatusText.Text = $"Loaded color profile {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load color profile failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveColorProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "MarkDNext color profile (*.json)|*.json|All files (*.*)|*.*",
            FileName = "MarkDNext.colors.json"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(_colorProfile, new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json, Utf8NoBom);
            StatusText.Text = $"Saved color profile {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save color profile failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetColorProfile_Click(object sender, RoutedEventArgs e)
    {
        _colorProfile = ColorProfile.Default;
        ApplyAppearance();
        RefreshRenderedShells();
        StatusText.Text = "Color profile reset.";
    }

    private void MicaMaterial_Click(object sender, RoutedEventArgs e)
    {
        SetWindowMaterial(WindowBackdropKind.Mica);
    }

    private void AcrylicMaterial_Click(object sender, RoutedEventArgs e)
    {
        SetWindowMaterial(WindowBackdropKind.Acrylic);
    }

    private void FlatMaterial_Click(object sender, RoutedEventArgs e)
    {
        SetWindowMaterial(WindowBackdropKind.Flat);
    }

    private void SetWindowMaterial(WindowBackdropKind kind)
    {
        _windowBackdrop = kind;
        WindowBackdrop.TryApply(this, kind);

        RootDock.Background = kind switch
        {
            WindowBackdropKind.Acrylic => new SolidColorBrush(Color.FromArgb(0xCC, 0xF8, 0xFA, 0xFC)),
            WindowBackdropKind.Mica => new SolidColorBrush(Color.FromArgb(0xDD, 0xF6, 0xF7, 0xF9)),
            _ => new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9))
        };

        MicaMaterialMenuItem.IsChecked = kind == WindowBackdropKind.Mica;
        AcrylicMaterialMenuItem.IsChecked = kind == WindowBackdropKind.Acrylic;
        FlatMaterialMenuItem.IsChecked = kind == WindowBackdropKind.Flat;
    }

    private void AdjustFontSize(double delta)
    {
        _editorFontSize = Math.Clamp(_editorFontSize + delta, 8, 48);
        _contentFontSize = Math.Clamp(_contentFontSize + delta, 10, 54);
        ApplyAppearance();
        RefreshRenderedShells();
        StatusText.Text = $"Font size: {_editorFontSize.ToString("0.#", CultureInfo.InvariantCulture)}";
    }

    private void ApplyAppearance()
    {
        Editor.FontFamily = new FontFamily(_editorFontFamily);
        Editor.FontSize = _editorFontSize;
        Editor.Foreground = BrushFromHex(_colorProfile.EditorText);
        Editor.Background = BrushFromHex(_colorProfile.EditorBackground);

        RootDock.Background = BrushFromHex(_colorProfile.Window);
        MainMenu.Background = BrushFromHex(_colorProfile.Chrome);
        MainStatusBar.Background = BrushFromHex(_colorProfile.Chrome);
        EditorPane.Background = BrushFromHex(_colorProfile.Surface);
        PreviewPane.Background = BrushFromHex(_colorProfile.Surface);
    }

    private void RefreshRenderedShells()
    {
        _previewShellReady = false;
        _wysiwygShellReady = false;
        ScheduleRender();
        if (_wysiwygMode)
        {
            _ = RenderWysiwygAsync();
        }
    }

    private static SolidColorBrush BrushFromHex(string hex, byte? alpha = null)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            if (alpha.HasValue)
            {
                color.A = alpha.Value;
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            var fallback = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9));
            fallback.Freeze();
            return fallback;
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "MarkDNext\n\nNative WPF Markdown editor and viewer with source editing, WYSIWYG block editing, KaTeX formulas, code highlighting, completion, file watching, and system print/PDF export.",
            "About MarkDNext",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (!_isLoadingDocument)
        {
            _isDirty = true;
            UpdateTitle();
        }

        UpdatePosition();
        ScheduleRender();

        if (_wysiwygMode && !_isUpdatingFromWysiwyg)
        {
            _ = RenderWysiwygAsync();
        }
    }

    private void Editor_TextArea_TextEntered(object? sender, TextCompositionEventArgs e)
    {
        if (_wysiwygMode)
        {
            return;
        }

        if (!_automaticCompletionEnabled)
        {
            return;
        }

        if (TryCompletePair(e.Text))
        {
            return;
        }

        if (e.Text is "#" or "$" or "`" or "[" or "!" or ">" or "-")
        {
            ShowCompletionWindow(replaceActivationText: true);
        }
    }

    private bool TryCompletePair(string text)
    {
        var pair = text switch
        {
            "$" => "$",
            "`" => "`",
            "[" => "]",
            "(" => ")",
            "\"" => "\"",
            "'" => "'",
            _ => null
        };

        if (pair is null)
        {
            return false;
        }

        Editor.Document.Insert(Editor.CaretOffset, pair);
        return true;
    }

    private void Editor_TextArea_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_wysiwygMode && e.Key == Key.Tab && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift)
            && TryIndentSourceListLine(Keyboard.Modifiers == ModifierKeys.Shift))
        {
            e.Handled = true;
            return;
        }

        if (!_wysiwygMode && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && TryHandleSourceListEnter())
        {
            e.Handled = true;
            return;
        }

        if (!_wysiwygMode && e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.None && TryMoveLastLineDownToLineEnd())
        {
            e.Handled = true;
            return;
        }

        if (!_wysiwygMode
            && Keyboard.Modifiers == ModifierKeys.Control
            && e.Key == Key.V
            && TryGetClipboardImageReference(out var source, out var alt))
        {
            _ = InsertImageReferenceAsync(source, alt);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Space)
        {
            ShowCompletionWindow(replaceActivationText: true);
            e.Handled = true;
        }
    }

    private bool TryHandleSourceListEnter()
    {
        if (Editor.SelectionLength > 0 || Editor.Document is null || IsCaretInsideFencedCodeBlock())
        {
            return false;
        }

        var caret = Editor.CaretOffset;
        var line = Editor.Document.GetLineByOffset(caret);
        var lineText = Editor.Document.GetText(line.Offset, line.Length);
        var column = Math.Clamp(caret - line.Offset, 0, lineText.Length);
        var before = lineText[..column];
        var after = lineText[column..];

        if (TryApplyBlankIndentedListEnter(line, before, after))
        {
            return true;
        }

        if (TryApplyTaskListEnter(line, before, after, caret))
        {
            return true;
        }

        if (TryApplyOrderedListEnter(line, before, after, caret))
        {
            return true;
        }

        return TryApplyBulletListEnter(line, before, after, caret);
    }

    private bool TryApplyTaskListEnter(DocumentLine line, string before, string after, int caret)
    {
        var match = Regex.Match(before, @"^(\s*)([-+*])\s+\[( |x|X)\]\s+(.*)$");
        if (!match.Success)
        {
            return false;
        }

        var indent = match.Groups[1].Value;
        var marker = match.Groups[2].Value;
        var content = match.Groups[4].Value;
        if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(after))
        {
            Editor.Document.Replace(line.Offset, line.Length, indent);
            Editor.CaretOffset = line.Offset + indent.Length;
            return true;
        }

        InsertSourceContinuation(caret, $"{indent}{marker} [ ] ");
        return true;
    }

    private bool TryApplyOrderedListEnter(DocumentLine line, string before, string after, int caret)
    {
        var match = Regex.Match(before, @"^(\s*)(\d+)([.)])\s+(.*)$");
        if (!match.Success)
        {
            return false;
        }

        var indent = match.Groups[1].Value;
        var number = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var delimiter = match.Groups[3].Value;
        var content = match.Groups[4].Value;
        if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(after))
        {
            Editor.Document.Replace(line.Offset, line.Length, indent);
            Editor.CaretOffset = line.Offset + indent.Length;
            return true;
        }

        InsertSourceContinuation(caret, $"{indent}{number + 1}{delimiter} ");
        return true;
    }

    private bool TryApplyBulletListEnter(DocumentLine line, string before, string after, int caret)
    {
        var match = Regex.Match(before, @"^(\s*)([-+*])\s+(.*)$");
        if (!match.Success)
        {
            return false;
        }

        var indent = match.Groups[1].Value;
        var marker = match.Groups[2].Value;
        var content = match.Groups[3].Value;
        if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(after))
        {
            Editor.Document.Replace(line.Offset, line.Length, indent);
            Editor.CaretOffset = line.Offset + indent.Length;
            return true;
        }

        InsertSourceContinuation(caret, $"{indent}{marker} ");
        return true;
    }

    private void InsertSourceContinuation(int caret, string continuation)
    {
        var insertion = Environment.NewLine + continuation;
        Editor.Document.Insert(caret, insertion);
        Editor.CaretOffset = caret + insertion.Length;
    }

    private bool TryApplyBlankIndentedListEnter(DocumentLine line, string before, string after)
    {
        if (before.Length == 0 || !before.All(char.IsWhiteSpace) || !string.IsNullOrWhiteSpace(after))
        {
            return false;
        }

        var parentIndent = OutdentListIndent(before);
        if (!TryGetContinuationFromPreviousListItem(line.LineNumber, parentIndent, out var replacement))
        {
            return false;
        }

        Editor.Document.Replace(line.Offset, line.Length, replacement);
        Editor.CaretOffset = line.Offset + replacement.Length;
        return true;
    }

    private bool TryIndentSourceListLine(bool outdent)
    {
        if (Editor.SelectionLength > 0 || Editor.Document is null || IsCaretInsideFencedCodeBlock())
        {
            return false;
        }

        var caret = Editor.CaretOffset;
        var line = Editor.Document.GetLineByOffset(caret);
        var lineText = Editor.Document.GetText(line.Offset, line.Length);
        var column = Math.Clamp(caret - line.Offset, 0, lineText.Length);

        if (TryReplaceSourceTaskIndent(line, lineText, column, outdent)
            || TryReplaceSourceOrderedIndent(line, lineText, column, outdent)
            || TryReplaceSourceBulletIndent(line, lineText, column, outdent))
        {
            return true;
        }

        return false;
    }

    private bool TryReplaceSourceTaskIndent(DocumentLine line, string lineText, int column, bool outdent)
    {
        var match = Regex.Match(lineText, @"^(\s*)([-+*])\s+\[( |x|X)\]\s*(.*)$");
        if (!match.Success)
        {
            return false;
        }

        var indent = match.Groups[1].Value;
        var marker = match.Groups[2].Value;
        var state = match.Groups[3].Value;
        var content = match.Groups[4].Value;
        var newIndent = ChangeListIndent(indent, outdent);
        if (newIndent == indent)
        {
            return true;
        }

        ReplaceListLine(line, lineText, column, match.Groups[4].Index, $"{newIndent}{marker} [{state}] ", content);
        return true;
    }

    private bool TryReplaceSourceOrderedIndent(DocumentLine line, string lineText, int column, bool outdent)
    {
        var match = Regex.Match(lineText, @"^(\s*)(\d+)([.)])\s*(.*)$");
        if (!match.Success)
        {
            return false;
        }

        var indent = match.Groups[1].Value;
        var delimiter = match.Groups[3].Value;
        var content = match.Groups[4].Value;
        var newIndent = ChangeListIndent(indent, outdent);
        if (newIndent == indent)
        {
            return true;
        }

        var number = outdent
            ? FindPreviousOrderedNumber(line.LineNumber, newIndent, delimiter) + 1
            : 1;
        ReplaceListLine(line, lineText, column, match.Groups[4].Index, $"{newIndent}{number.ToString(CultureInfo.InvariantCulture)}{delimiter} ", content);
        return true;
    }

    private bool TryReplaceSourceBulletIndent(DocumentLine line, string lineText, int column, bool outdent)
    {
        var match = Regex.Match(lineText, @"^(\s*)([-+*])\s*(.*)$");
        if (!match.Success)
        {
            return false;
        }

        var indent = match.Groups[1].Value;
        var marker = match.Groups[2].Value;
        var content = match.Groups[3].Value;
        var newIndent = ChangeListIndent(indent, outdent);
        if (newIndent == indent)
        {
            return true;
        }

        ReplaceListLine(line, lineText, column, match.Groups[3].Index, $"{newIndent}{marker} ", content);
        return true;
    }

    private void ReplaceListLine(DocumentLine line, string oldLineText, int oldColumn, int oldContentColumn, string newPrefix, string content)
    {
        var replacement = newPrefix + content;
        Editor.Document.Replace(line.Offset, line.Length, replacement);
        var newColumn = oldColumn <= oldContentColumn
            ? newPrefix.Length
            : newPrefix.Length + oldColumn - oldContentColumn;
        Editor.CaretOffset = line.Offset + Math.Clamp(newColumn, 0, replacement.Length);
    }

    private string ChangeListIndent(string indent, bool outdent)
    {
        return outdent
            ? OutdentListIndent(indent)
            : indent + new string(' ', ListIndentSize);
    }

    private static string OutdentListIndent(string indent)
    {
        if (indent.Length == 0)
        {
            return string.Empty;
        }

        if (indent.EndsWith('\t'))
        {
            return indent[..^1];
        }

        return indent.Length <= ListIndentSize
            ? string.Empty
            : indent[..^ListIndentSize];
    }

    private int FindPreviousOrderedNumber(int currentLineNumber, string indent, string delimiter)
    {
        if (Editor.Document is null)
        {
            return 0;
        }

        for (var lineNumber = currentLineNumber - 1; lineNumber >= 1; lineNumber--)
        {
            var line = Editor.Document.GetLineByNumber(lineNumber);
            var text = Editor.Document.GetText(line.Offset, line.Length);
            var match = Regex.Match(text, @"^(\s*)(\d+)([.)])\s+");
            if (!match.Success)
            {
                continue;
            }

            if (match.Groups[1].Value == indent && match.Groups[3].Value == delimiter
                && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
        }

        return 0;
    }

    private bool TryGetContinuationFromPreviousListItem(int currentLineNumber, string indent, out string continuation)
    {
        continuation = string.Empty;
        if (Editor.Document is null)
        {
            return false;
        }

        for (var lineNumber = currentLineNumber - 1; lineNumber >= 1; lineNumber--)
        {
            var line = Editor.Document.GetLineByNumber(lineNumber);
            var text = Editor.Document.GetText(line.Offset, line.Length);
            var task = Regex.Match(text, @"^(\s*)([-+*])\s+\[( |x|X)\]\s+");
            if (task.Success && task.Groups[1].Value == indent)
            {
                continuation = $"{indent}{task.Groups[2].Value} [ ] ";
                return true;
            }

            var ordered = Regex.Match(text, @"^(\s*)(\d+)([.)])\s+");
            if (ordered.Success && ordered.Groups[1].Value == indent
                && int.TryParse(ordered.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                continuation = $"{indent}{(number + 1).ToString(CultureInfo.InvariantCulture)}{ordered.Groups[3].Value} ";
                return true;
            }

            var bullet = Regex.Match(text, @"^(\s*)([-+*])\s+");
            if (bullet.Success && bullet.Groups[1].Value == indent)
            {
                continuation = $"{indent}{bullet.Groups[2].Value} ";
                return true;
            }
        }

        return false;
    }

    private bool IsCaretInsideFencedCodeBlock()
    {
        if (Editor.Document is null)
        {
            return false;
        }

        var caretLine = Editor.Document.GetLineByOffset(Editor.CaretOffset).LineNumber;
        var inFence = false;
        for (var lineNumber = 1; lineNumber < caretLine; lineNumber++)
        {
            var line = Editor.Document.GetLineByNumber(lineNumber);
            var text = Editor.Document.GetText(line.Offset, line.Length).TrimStart();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
        }

        return inFence;
    }

    private bool TryMoveLastLineDownToLineEnd()
    {
        if (Editor.Document is null)
        {
            return false;
        }

        var caretLine = Editor.TextArea.Caret.Line;
        if (caretLine != Editor.Document.LineCount)
        {
            return false;
        }

        var line = Editor.Document.GetLineByNumber(caretLine);
        Editor.CaretOffset = line.EndOffset;
        return true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            New_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            Open_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
        {
            SaveAs_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            ShowFindBar();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Space)
        {
            ShowCompletionWindow(replaceActivationText: true);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            Undo_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
        {
            Redo_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            IncreaseFont_Click(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            DecreaseFont_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            _ = FindAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            Reload_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && FindBar.Visibility == Visibility.Visible)
        {
            HideFindBar();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.P)
        {
            Print_Click(sender, e);
            e.Handled = true;
        }
    }

    private async void FindTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await FindAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideFindBar();
            e.Handled = true;
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var image = files.FirstOrDefault(file => File.Exists(file) && IsImageFile(file));
            if (image is not null)
            {
                await InsertImageAsync(image);
                e.Handled = true;
                return;
            }

            var file = files.FirstOrDefault(file => File.Exists(file) && IsMarkdownFile(file));
            if (file is not null && ConfirmSaveChanges())
            {
                LoadFile(file);
                e.Handled = true;
                return;
            }
        }

        if (TryGetImageReference(e.Data, out var source, out var alt))
        {
            await InsertImageReferenceAsync(source, alt);
            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmSaveChanges())
        {
            e.Cancel = true;
            return;
        }

        _watcher?.Dispose();
    }

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        _renderTimer.Stop();
        _ = RenderPreviewAsync();
    }

    private async void PreviewScrollTimer_Tick(object? sender, EventArgs e)
    {
        _previewScrollTimer.Stop();
        await SyncPreviewScrollToCaretAsync();
    }

    private void WatchTimer_Tick(object? sender, EventArgs e)
    {
        _watchTimer.Stop();
        HandleWatchedFileChanged();
    }

    private void NewDocument()
    {
        _watcher?.Dispose();
        _watcher = null;
        _currentFilePath = null;
        _lastDiskWriteUtc = DateTime.MinValue;
        _isLoadingDocument = true;
        Editor.Text = "# Untitled\r\n\r\nStart writing Markdown here.\r\n\r\nInline math: $\\alpha$.\r\n\r\n$$\\alpha$$\r\n";
        _isLoadingDocument = false;
        _isDirty = false;
        UpdateTitle();
        UpdatePosition();
        StatusText.Text = "New document";
        ScheduleRender();
        if (_wysiwygMode)
        {
            _ = RenderWysiwygAsync();
        }
        else
        {
            Editor.TextArea.Focus();
        }
    }

    private void LoadFile(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var text = File.ReadAllText(fullPath, Encoding.UTF8);

            _isLoadingDocument = true;
            Editor.Text = text;
            Editor.CaretOffset = 0;
            Editor.ScrollToHome();
            _isLoadingDocument = false;

            _currentFilePath = fullPath;
            _lastDiskWriteUtc = File.GetLastWriteTimeUtc(fullPath);
            _isDirty = false;

            WatchCurrentFile();
            UpdateTitle();
            UpdatePosition();
            StatusText.Text = $"Opened {Path.GetFileName(fullPath)}";
            ScheduleRender();
            if (_wysiwygMode)
            {
                _ = RenderWysiwygAsync();
            }
        }
        catch (Exception ex)
        {
            _isLoadingDocument = false;
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool SaveDocument()
    {
        if (_currentFilePath is null)
        {
            return SaveDocumentAs();
        }

        return SaveToPath(_currentFilePath);
    }

    private bool SaveDocumentAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Markdown file (*.md)|*.md|Markdown file (*.markdown)|*.markdown|Text file (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = _currentFilePath is null ? "Untitled.md" : Path.GetFileName(_currentFilePath)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        return SaveToPath(dialog.FileName);
    }

    private bool SaveToPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            File.WriteAllText(fullPath, Editor.Text, Utf8NoBom);
            _currentFilePath = fullPath;
            _lastDiskWriteUtc = File.GetLastWriteTimeUtc(fullPath);
            _isDirty = false;
            WatchCurrentFile();
            UpdateTitle();
            StatusText.Text = $"Saved {Path.GetFileName(fullPath)}";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool ConfirmSaveChanges()
    {
        if (!_isDirty)
        {
            return true;
        }

        var name = _currentFilePath is null ? "Untitled.md" : Path.GetFileName(_currentFilePath);
        var result = MessageBox.Show(
            this,
            $"Save changes to {name}?",
            "Unsaved changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        return result switch
        {
            MessageBoxResult.Yes => SaveDocument(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void ReloadFromDisk(bool showStatus)
    {
        if (_currentFilePath is null || !File.Exists(_currentFilePath))
        {
            return;
        }

        try
        {
            _isLoadingDocument = true;
            var caret = Editor.CaretOffset;
            Editor.Text = File.ReadAllText(_currentFilePath, Encoding.UTF8);
            Editor.CaretOffset = Math.Min(caret, Editor.Text.Length);
            _isLoadingDocument = false;
            _lastDiskWriteUtc = File.GetLastWriteTimeUtc(_currentFilePath);
            _isDirty = false;
            UpdateTitle();
            UpdatePosition();
            ScheduleRender();
            if (_wysiwygMode)
            {
                _ = RenderWysiwygAsync();
            }

            if (showStatus)
            {
                StatusText.Text = $"Reloaded {Path.GetFileName(_currentFilePath)}";
            }
        }
        catch (Exception ex)
        {
            _isLoadingDocument = false;
            StatusText.Text = "Reload failed.";
            MessageBox.Show(this, ex.Message, "Reload failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void WatchCurrentFile()
    {
        _watcher?.Dispose();
        _watcher = null;

        if (_currentFilePath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_currentFilePath);
        var fileName = Path.GetFileName(_currentFilePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        _watcher = new FileSystemWatcher(directory)
        {
            Filter = fileName,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
        };
        _watcher.Changed += FileWatcher_Changed;
        _watcher.Created += FileWatcher_Changed;
        _watcher.Renamed += FileWatcher_Changed;
        _watcher.Deleted += FileWatcher_Changed;
        _watcher.EnableRaisingEvents = true;
    }

    private void FileWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _watchTimer.Stop();
            _watchTimer.Start();
        });
    }

    private void HandleWatchedFileChanged()
    {
        if (_currentFilePath is null)
        {
            return;
        }

        if (!File.Exists(_currentFilePath))
        {
            StatusText.Text = "The file was removed or renamed on disk.";
            return;
        }

        DateTime diskStamp;
        try
        {
            diskStamp = File.GetLastWriteTimeUtc(_currentFilePath);
        }
        catch
        {
            return;
        }

        if (diskStamp == _lastDiskWriteUtc)
        {
            return;
        }

        if (_isDirty)
        {
            StatusText.Text = "The file changed on disk. Save or reload to resolve.";
            return;
        }

        ReloadFromDisk(showStatus: true);
    }

    private void ScheduleRender()
    {
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private void SchedulePreviewScrollSync()
    {
        if (!ShouldSyncPreviewToCaret())
        {
            return;
        }

        _previewScrollTimer.Stop();
        _previewScrollTimer.Start();
    }

    private bool ShouldSyncPreviewToCaret()
    {
        return !_wysiwygMode
            && _viewMode == ViewMode.Both
            && _previewReady
            && Preview.CoreWebView2 is not null
            && Preview.Visibility == Visibility.Visible
            && Editor.TextArea.IsKeyboardFocusWithin;
    }

    private async Task SyncPreviewScrollToCaretAsync()
    {
        if (!ShouldSyncPreviewToCaret() || Preview.CoreWebView2 is null)
        {
            return;
        }

        var line = Math.Max(1, Editor.TextArea.Caret.Line);
        var script = $"window.mdvSyncToLine ? window.mdvSyncToLine({line}, false) : false;";
        await Preview.CoreWebView2.ExecuteScriptAsync(script);
    }

    private async Task RenderPreviewAsync()
    {
        if (_previewFailed || !_previewReady)
        {
            return;
        }

        if (_isRenderingPreview)
        {
            _renderAgainRequested = true;
            return;
        }

        _isRenderingPreview = true;
        try
        {
            do
            {
                _renderAgainRequested = false;
                MapCommonFolders(Preview.CoreWebView2);

                var htmlBody = RenderMarkdownToPreviewHtml(Editor.Text);
                var syncLine = ShouldSyncPreviewToCaret()
                    ? Math.Max(1, Editor.TextArea.Caret.Line)
                    : (int?)null;
                await EnsurePreviewShellAsync();
                await SetPreviewBodyAsync(htmlBody, syncLine);
            }
            while (_renderAgainRequested);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Preview render failed.";
            Debug.WriteLine(ex);
        }
        finally
        {
            _isRenderingPreview = false;
        }
    }

    private async Task RenderWysiwygAsync()
    {
        if (!_wysiwygMode || !_wysiwygReady || Wysiwyg.CoreWebView2 is null)
        {
            return;
        }

        if (_isRenderingWysiwyg)
        {
            _wysiwygRenderAgainRequested = true;
            return;
        }

        _isRenderingWysiwyg = true;
        try
        {
            do
            {
                _wysiwygRenderAgainRequested = false;
                MapCommonFolders(Wysiwyg.CoreWebView2);
                await EnsureWysiwygShellAsync();
                await SetWysiwygBlocksAsync(BuildWysiwygBlocks(Editor.Text));
            }
            while (_wysiwygRenderAgainRequested);
        }
        catch (Exception ex)
        {
            StatusText.Text = "WYSIWYG render failed.";
            Debug.WriteLine(ex);
        }
        finally
        {
            _isRenderingWysiwyg = false;
        }
    }

    private async Task EnsurePreviewShellAsync()
    {
        if (_previewShellReady || Preview.CoreWebView2 is null)
        {
            return;
        }

        await NavigateToStringAsync(Preview, BuildPreviewDocument());
        _previewShellReady = true;
    }

    private async Task SetPreviewBodyAsync(string htmlBody, int? syncLine = null)
    {
        if (Preview.CoreWebView2 is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(htmlBody, JsonOptions);
        var linePayload = syncLine.HasValue
            ? syncLine.Value.ToString(CultureInfo.InvariantCulture)
            : "null";
        var script = $"window.mdvSetPreview ? window.mdvSetPreview({payload}, {linePayload}) : false;";
        await Preview.CoreWebView2.ExecuteScriptAsync(script);
    }

    private async Task EnsureWysiwygShellAsync()
    {
        if (_wysiwygShellReady || Wysiwyg.CoreWebView2 is null)
        {
            return;
        }

        await NavigateToStringAsync(Wysiwyg, BuildWysiwygDocument());
        _wysiwygShellReady = true;
    }

    private async Task SetWysiwygBlocksAsync(IReadOnlyList<WysiwygBlock> blocks)
    {
        if (Wysiwyg.CoreWebView2 is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(blocks, JsonOptions).Replace("</", "<\\/", StringComparison.Ordinal);
        var script = $"window.mdvSetBlocks ? window.mdvSetBlocks({payload}) : false;";
        await Wysiwyg.CoreWebView2.ExecuteScriptAsync(script);
    }

    private async Task NavigateToStringAsync(WpfWebView2 webView, string html)
    {
        if (webView.CoreWebView2 is null)
        {
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            webView.CoreWebView2.NavigationCompleted -= NavigationCompleted;
            completion.TrySetResult(args.IsSuccess);
        }

        webView.CoreWebView2.NavigationCompleted += NavigationCompleted;
        webView.NavigateToString(html);

        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            webView.CoreWebView2.NavigationCompleted -= NavigationCompleted;
        }
    }

    private string ContentFontCss => CssFontFamily(_contentFontFamily, "system-ui, sans-serif");

    private string EditorFontCss => CssFontFamily(_editorFontFamily, "Consolas, \"Cascadia Mono\", \"SFMono-Regular\", monospace");

    private string ContentFontSizeCss => _contentFontSize.ToString("0.###", CultureInfo.InvariantCulture);

    private string EditorFontSizeCss => _editorFontSize.ToString("0.###", CultureInfo.InvariantCulture);

    private static string CssFontFamily(string family, string fallback)
    {
        var clean = string.IsNullOrWhiteSpace(family) ? "Segoe UI" : family.Trim();
        clean = clean.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{clean}\", {fallback}";
    }

    private string BuildPreviewDocument()
    {
        var title = WebUtility.HtmlEncode(_currentFilePath is null ? "Untitled" : Path.GetFileName(_currentFilePath));
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        return $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'self' https://{{DocumentHost}} https://{{AssetHost}} data: blob:; img-src 'self' https://{{DocumentHost}} https://{{AssetHost}} https: http: data: file: blob:; script-src 'nonce-{{nonce}}' https://{{AssetHost}} 'unsafe-eval'; style-src 'unsafe-inline' https://{{AssetHost}}; font-src data: https://{{AssetHost}};">
<base href="https://{{DocumentHost}}/">
<title>{{title}}</title>
<link rel="stylesheet" href="https://{{AssetHost}}/highlight-github.min.css">
<link rel="stylesheet" href="https://{{AssetHost}}/katex/katex.min.css">
<style>
:root {
  color-scheme: light;
  --page: {{_colorProfile.Page}};
  --text: {{_colorProfile.Text}};
  --muted: {{_colorProfile.Muted}};
  --line: {{_colorProfile.Line}};
  --code: {{_colorProfile.Code}};
  --accent: {{_colorProfile.Accent}};
  --surface: {{_colorProfile.Surface}};
  --quote-bg: {{_colorProfile.QuoteBackground}};
  --heading: {{_colorProfile.Heading}};
}
html, body {
  margin: 0;
  min-height: 100%;
  max-width: 100%;
  overflow-x: hidden;
  overflow-y: auto;
  overscroll-behavior-x: none;
  background: var(--page);
  color: var(--text);
  font: {{ContentFontSizeCss}}px/1.62 {{ContentFontCss}};
}
.markdown-body {
  box-sizing: border-box;
  width: 100%;
  max-width: 960px;
  overflow-x: hidden;
  overscroll-behavior-x: none;
  margin: 0 auto;
  padding: 34px 42px 72px;
}
.markdown-body > :first-child { margin-top: 0; }
.mdv-preview-block {
  display: flow-root;
}
.mdv-preview-block:first-child > :first-child {
  margin-top: 0;
}
.mdv-preview-block:last-child > :last-child {
  margin-bottom: 0;
}
.markdown-body h1,
.markdown-body h2,
.markdown-body h3 {
  line-height: 1.25;
  margin: 1.5em 0 .6em;
  color: var(--heading);
}
.markdown-body h1 {
  padding-bottom: .32em;
  border-bottom: 1px solid var(--line);
  font-size: 2.1em;
}
.markdown-body h2 {
  padding-bottom: .25em;
  border-bottom: 1px solid var(--line);
  font-size: 1.55em;
}
.markdown-body h3 { font-size: 1.25em; }
.markdown-body p,
.markdown-body ul,
.markdown-body ol,
.markdown-body blockquote,
.markdown-body pre,
.markdown-body table {
  margin-top: 0;
  margin-bottom: 1em;
}
.markdown-body a {
  color: var(--accent);
  text-decoration: none;
}
.markdown-body a:hover { text-decoration: underline; }
.markdown-body blockquote {
  border-left: 4px solid #9fb2c7;
  color: var(--muted);
  padding: .1em 1em;
  margin-left: 0;
  background: var(--quote-bg);
}
.markdown-body code {
  font-family: {{EditorFontCss}};
  background: var(--code);
  border-radius: 4px;
  padding: .13em .32em;
  font-size: .92em;
}
.markdown-body pre {
  overflow: auto;
  overscroll-behavior-x: contain;
  background: var(--code);
  border: 0;
  border-radius: 0;
  padding: 14px 16px;
}
.markdown-body pre code {
  background: transparent;
  border: 0;
  padding: 0;
  font-size: .9em;
}
.markdown-body table {
  width: max-content;
  max-width: 100%;
  border-collapse: collapse;
  display: block;
  overflow-x: auto;
  overscroll-behavior-x: contain;
}
.markdown-body th,
.markdown-body td {
  border: 1px solid var(--line);
  padding: 6px 10px;
}
.markdown-body tr:nth-child(2n) { background: var(--quote-bg); }
.markdown-body img {
  max-width: 100%;
  height: auto;
}
.mdv-front-matter {
  box-sizing: border-box;
  max-width: 100%;
  overflow: hidden;
  color: var(--muted);
  background: var(--quote-bg);
  border: 1px solid var(--line);
  border-radius: 6px;
  padding: 10px 12px;
  margin-bottom: 1em;
}
.mdv-front-matter-title {
  color: var(--heading);
  font-weight: 600;
  margin-bottom: 6px;
}
.mdv-front-matter table {
  width: 100%;
  max-width: 100%;
  table-layout: fixed;
  border-collapse: collapse;
  display: table;
  margin: 0;
}
.mdv-front-matter th {
  width: 11rem;
  max-width: 38%;
  white-space: normal;
  text-align: left;
  vertical-align: top;
}
.mdv-front-matter th,
.mdv-front-matter td {
  min-width: 0;
  overflow-wrap: anywhere;
  word-break: break-word;
  vertical-align: top;
}
.markdown-body hr {
  border: 0;
  border-top: 1px solid var(--line);
  margin: 2em 0;
}
.markdown-body input[type="checkbox"] {
  transform: translateY(1px);
  margin-right: .45em;
}
.markdown-body li.mdv-task-item {
  list-style-type: none;
}
.markdown-body li.mdv-task-item > input[type="checkbox"] {
  cursor: pointer;
  margin-left: -1.35em;
}
.math-row {
  box-sizing: border-box;
  display: block;
  overflow: visible;
  width: 100%;
  margin: 1.1em 0;
  padding: 2px 0;
  text-align: center;
}
.math-shell {
  box-sizing: border-box;
  display: block;
  width: 100%;
  text-align: center;
}
.math-display {
  box-sizing: border-box;
  display: block;
  width: 100%;
  max-width: 100%;
  min-width: 0;
  margin: 1.1em 0;
  padding: 0;
  text-align: center;
}
.math-display .katex-display {
  display: block;
  width: 100%;
  margin: 0 auto !important;
  max-width: 100%;
  min-width: 0;
  text-align: center;
}
.math-display .katex {
  display: inline-block !important;
  text-align: center !important;
}
.math-inline {
  white-space: nowrap;
}
.math {
  visibility: hidden;
}
.math.math-ready {
  visibility: visible;
}
.render-buffer {
  box-sizing: border-box;
  position: absolute;
  left: -100000px;
  top: 0;
  visibility: hidden;
  pointer-events: none;
}
@media print {
  .markdown-body {
    max-width: none;
    padding: 0;
  }
  a { color: inherit; text-decoration: none; }
}
</style>
</head>
<body>
<main id="content" class="markdown-body"></main>
<script src="https://{{AssetHost}}/highlight.min.js"></script>
<script src="https://{{AssetHost}}/katex/katex.min.js"></script>
<script nonce="{{nonce}}">
let previewRenderToken = 0;

function highlightCode(root) {
  if (!window.hljs) return;
  root.querySelectorAll('pre code').forEach(function (block) {
    block.removeAttribute('data-highlighted');
    window.hljs.highlightElement(block);
  });
}

async function typesetMath(root) {
  const mathNodes = Array.from(root.querySelectorAll ? root.querySelectorAll('.math') : []);
  if (!mathNodes.length) return;
  mathNodes.forEach(function (node) {
    const source = mathSource(node);
    node.classList.remove('math-ready');
    if (window.katex) {
      window.katex.render(source, node, {
        displayMode: node.classList.contains('math-display'),
        throwOnError: false,
        output: 'htmlAndMathml'
      });
    }
    node.classList.add('math-ready');
  });
  centerDisplayMath(root);
}

function mathSource(node) {
  let source = (node.textContent || '').trim();
  if (source.startsWith('$$') && source.endsWith('$$')) {
    return source.slice(2, -2).trim();
  }
  if (source.startsWith('$') && source.endsWith('$')) {
    return source.slice(1, -1).trim();
  }
  return source;
}

function normalizeDisplayMath(root) {
  return root;
}

function centerDisplayMath(root) {
  const displays = Array.from(root.querySelectorAll ? root.querySelectorAll('.math-display') : []);
  displays.forEach(function (display) {
    display.style.display = 'block';
    display.style.width = '100%';
    display.style.textAlign = 'center';
    const katexDisplay = display.querySelector('.katex-display');
    if (katexDisplay) {
      katexDisplay.style.width = '100%';
      katexDisplay.style.textAlign = 'center';
      katexDisplay.style.marginLeft = 'auto';
      katexDisplay.style.marginRight = 'auto';
    }
    const katex = display.querySelector('.katex-display > .katex');
    if (katex) {
      katex.style.display = 'inline-block';
      katex.style.textAlign = 'initial';
      katex.style.maxWidth = '100%';
    }
    const katexHtml = display.querySelector('.katex-display > .katex > .katex-html');
    if (katexHtml) {
      katexHtml.style.display = 'inline-block';
      katexHtml.style.position = 'relative';
      katexHtml.style.textAlign = 'left';
    }
  });
}

function wirePreviewTaskCheckboxes(root) {
  const boxes = Array.from(root.querySelectorAll('input[type="checkbox"]'));
  boxes.forEach(function (box, taskIndex) {
    const item = box.closest('li');
    if (item) item.classList.add('mdv-task-item');
    box.disabled = false;
    box.addEventListener('click', function (event) {
      event.stopPropagation();
    });
    box.addEventListener('change', function () {
      if (!window.chrome || !window.chrome.webview) return;
      window.chrome.webview.postMessage({ type: 'previewTask', taskIndex: taskIndex, checked: box.checked });
    });
  });
}

function previewBlockLine(block, name) {
  return parseInt(block.dataset[name] || '0', 10) || 0;
}

function previewBlocks() {
  return Array.from(document.querySelectorAll('.mdv-preview-block'));
}

window.mdvSyncToLine = function (sourceLine, smooth) {
  const line = Math.max(1, parseInt(sourceLine || 1, 10) || 1);
  const blocks = previewBlocks();
  if (!blocks.length) return false;

  let target = blocks.find(function (block) {
    const start = previewBlockLine(block, 'startLine');
    const end = previewBlockLine(block, 'endLine');
    return start <= line && line <= end;
  });

  if (!target) {
    target = blocks.reduce(function (best, block) {
      const bestDistance = Math.abs(previewBlockLine(best, 'startLine') - line);
      const distance = Math.abs(previewBlockLine(block, 'startLine') - line);
      return distance < bestDistance ? block : best;
    }, blocks[0]);
  }

  const start = previewBlockLine(target, 'startLine');
  const end = Math.max(start, previewBlockLine(target, 'endLine'));
  const span = Math.max(1, end - start + 1);
  const localRatio = Math.max(0, Math.min(1, (line - start + 0.35) / span));
  const rect = target.getBoundingClientRect();
  const absoluteTop = rect.top + window.scrollY;
  const desired = absoluteTop + rect.height * localRatio - window.innerHeight * 0.35;
  const maxScroll = Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
  window.scrollTo({ top: Math.max(0, Math.min(maxScroll, desired)), behavior: smooth ? 'smooth' : 'auto' });
  return true;
};

window.mdvSetPreview = async function (html, sourceLine) {
  const token = ++previewRenderToken;
  const content = document.getElementById('content');
  const scrollRatio = document.documentElement.scrollHeight <= window.innerHeight
    ? 0
    : window.scrollY / (document.documentElement.scrollHeight - window.innerHeight);

  const buffer = document.createElement('main');
  buffer.className = 'markdown-body render-buffer';
  buffer.style.width = content.getBoundingClientRect().width + 'px';
  buffer.innerHTML = html || '';
  normalizeDisplayMath(buffer);
  wirePreviewTaskCheckboxes(buffer);
  document.body.appendChild(buffer);

  try {
    highlightCode(buffer);
    await typesetMath(buffer);
    if (token !== previewRenderToken) {
      buffer.remove();
      return false;
    }

    content.replaceChildren(...Array.from(buffer.childNodes));
    const maxScroll = Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
    if (sourceLine) {
      window.mdvSyncToLine(sourceLine, false);
    } else {
      window.scrollTo(0, Math.min(maxScroll, maxScroll * scrollRatio));
    }
    return true;
  } finally {
    buffer.remove();
  }
};
</script>
</body>
</html>
""";
    }

    private string RenderMarkdownToPreviewHtml(string markdown)
    {
        var blocks = SplitMarkdownBlocksWithLines(markdown);
        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        var html = new StringBuilder();
        foreach (var block in blocks)
        {
            html.Append("<section class=\"mdv-preview-block\" data-start-line=\"")
                .Append(block.StartLine.ToString(CultureInfo.InvariantCulture))
                .Append("\" data-end-line=\"")
                .Append(block.EndLine.ToString(CultureInfo.InvariantCulture))
                .Append("\">")
                .Append(RenderMarkdownToHtml(block.Source))
                .AppendLine("</section>");
        }

        return html.ToString();
    }

    private string RenderMarkdownToHtml(string markdown)
    {
        if (IsFrontMatterBlock(markdown))
        {
            return RenderFrontMatterHtml(markdown);
        }

        var protectedMarkdown = ExtractMathSegments(markdown, out var mathSegments);
        protectedMarkdown = ExtractImageSegments(protectedMarkdown, out var imageSegments);
        var html = Markdown.ToHtml(protectedMarkdown, _markdownPipeline);

        foreach (var segment in mathSegments)
        {
            var encodedMath = WebUtility.HtmlEncode(segment.Source);
            if (segment.Display)
            {
                var display = $"<div class=\"math math-display\">{encodedMath}</div>";
                html = html.Replace($"<p>{segment.Placeholder}</p>", display, StringComparison.Ordinal);
                html = html.Replace(segment.Placeholder, display, StringComparison.Ordinal);
            }
            else
            {
                html = html.Replace(segment.Placeholder, $"<span class=\"math math-inline\">{encodedMath}</span>", StringComparison.Ordinal);
            }
        }

        foreach (var segment in imageSegments)
        {
            var imageHtml = RenderImageSegment(segment);
            html = html.Replace($"<p>{segment.Placeholder}</p>", $"<p>{imageHtml}</p>", StringComparison.Ordinal);
            html = html.Replace(segment.Placeholder, imageHtml, StringComparison.Ordinal);
        }

        return ResolveImageSources(html);
    }

    private static bool IsFrontMatterBlock(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return lines.Length >= 3
            && lines[0].Trim() == "---"
            && lines[^1].Trim() == "---";
    }

    private static string RenderFrontMatterHtml(string markdown)
    {
        var normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var body = lines.Skip(1).Take(Math.Max(0, lines.Length - 2)).ToArray();
        var rows = new StringBuilder();
        foreach (var line in body)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var split = line.IndexOf(':');
            if (split > 0)
            {
                rows.Append("<tr><th>")
                    .Append(WebUtility.HtmlEncode(line[..split].Trim()))
                    .Append("</th><td>")
                    .Append(WebUtility.HtmlEncode(line[(split + 1)..].Trim()))
                    .AppendLine("</td></tr>");
            }
            else
            {
                rows.Append("<tr><td colspan=\"2\">")
                    .Append(WebUtility.HtmlEncode(line))
                    .AppendLine("</td></tr>");
            }
        }

        if (rows.Length == 0)
        {
            return "<section class=\"mdv-front-matter\"><div class=\"mdv-front-matter-title\">Front matter</div><pre></pre></section>";
        }

        return "<section class=\"mdv-front-matter\"><div class=\"mdv-front-matter-title\">Front matter</div><table>"
            + rows
            + "</table></section>";
    }

    private string ResolveImageSources(string html)
    {
        return Regex.Replace(
            html,
            "(<img\\b[^>]*?\\bsrc\\s*=\\s*)(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))",
            match =>
            {
                var src = match.Groups[2].Success
                    ? match.Groups[2].Value
                    : match.Groups[3].Success
                        ? match.Groups[3].Value
                        : match.Groups[4].Value;
                src = WebUtility.HtmlDecode(src);
                var resolved = ResolveImageSource(src);
                return match.Groups[1].Value
                    + "\""
                    + WebUtility.HtmlEncode(resolved)
                    + "\"";
            },
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private string ResolveImageSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)
            || source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith($"https://{DocumentHost}/", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith($"https://{AssetHost}/", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        var normalized = source.Replace('\\', '/');
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath))
            {
                return ToImageDataUri(uri.LocalPath);
            }

            if (Path.IsPathFullyQualified(source) && File.Exists(source))
            {
                return ToImageDataUri(source);
            }

            if (!Uri.TryCreate(source, UriKind.Absolute, out _))
            {
                var localPath = ResolveDocumentRelativePath(source);
                if (localPath is not null && File.Exists(localPath))
                {
                    return ToImageDataUri(localPath);
                }

                return BuildDocumentResourceUri(normalized);
            }
        }
        catch
        {
        }

        return normalized;
    }

    private string? ResolveDocumentRelativePath(string source)
    {
        var clean = source.Split('#', 2)[0].Split('?', 2)[0];
        clean = Uri.UnescapeDataString(clean)
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(clean) || Path.IsPathFullyQualified(clean))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(GetDocumentBaseFolder(), clean));
        }
        catch
        {
            return null;
        }
    }

    private static string ToImageDataUri(string path)
    {
        try
        {
            var mime = GetImageMimeType(path);
            var bytes = File.ReadAllBytes(path);
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }
    }

    private static string GetImageMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }

    private static string GetAssetMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".ttf" => "font/ttf",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
    }

    private static string BuildDocumentResourceUri(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        var encoded = string.Join(
            "/",
            normalized
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => Uri.EscapeDataString(Uri.UnescapeDataString(part))));
        return $"https://{DocumentHost}/{encoded}";
    }

    private string RenderImageSegment(ImageSegment segment)
    {
        var src = WebUtility.HtmlEncode(ResolveImageSource(segment.Target));
        var alt = WebUtility.HtmlEncode(segment.Alt);
        var title = string.IsNullOrWhiteSpace(segment.Title)
            ? string.Empty
            : $" title=\"{WebUtility.HtmlEncode(segment.Title)}\"";
        return $"<img src=\"{src}\" alt=\"{alt}\"{title}>";
    }

    private static string ExtractImageSegments(string markdown, out List<ImageSegment> segments)
    {
        segments = new List<ImageSegment>();
        var builder = new StringBuilder(markdown.Length);
        var inFence = false;
        var inInlineCode = false;

        for (var i = 0; i < markdown.Length;)
        {
            if (IsAtLineStart(markdown, i) && StartsWithAt(markdown, i, "```"))
            {
                inFence = !inFence;
                builder.Append("```");
                i += 3;
                continue;
            }

            if (!inFence && markdown[i] == '`')
            {
                inInlineCode = !inInlineCode;
                builder.Append(markdown[i]);
                i++;
                continue;
            }

            if (!inFence
                && !inInlineCode
                && StartsWithAt(markdown, i, "![")
                && (i == 0 || markdown[i - 1] != '\\')
                && TryReadImageSegment(markdown, i, segments.Count, out var segment, out var end))
            {
                segments.Add(segment);
                builder.Append(segment.Placeholder);
                i = end;
                continue;
            }

            builder.Append(markdown[i]);
            i++;
        }

        return builder.ToString();
    }

    private static bool TryReadImageSegment(
        string markdown,
        int start,
        int index,
        out ImageSegment segment,
        out int end)
    {
        segment = default!;
        end = start;

        var altEnd = FindClosingMarkdownBracket(markdown, start + 2, '[', ']');
        if (altEnd < 0 || altEnd + 1 >= markdown.Length || markdown[altEnd + 1] != '(')
        {
            return false;
        }

        var targetStart = altEnd + 2;
        var targetEnd = FindImageTargetEnd(markdown, targetStart);
        if (targetEnd < 0)
        {
            return false;
        }

        var alt = markdown.Substring(start + 2, altEnd - start - 2);
        var rawTarget = markdown.Substring(targetStart, targetEnd - targetStart);
        var (target, title) = SplitImageTarget(rawTarget);
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        segment = new ImageSegment(CreateImagePlaceholder(index), alt, target, title);
        end = targetEnd + 1;
        return true;
    }

    private static int FindClosingMarkdownBracket(string text, int start, char open, char close)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == open)
            {
                depth++;
                continue;
            }

            if (text[i] == close)
            {
                if (depth == 0)
                {
                    return i;
                }

                depth--;
            }
        }

        return -1;
    }

    private static int FindImageTargetEnd(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '\r' || text[i] == '\n')
            {
                return -1;
            }

            if (text[i] == ')')
            {
                return i;
            }
        }

        return -1;
    }

    private static (string Target, string? Title) SplitImageTarget(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
        {
            return (WebUtility.HtmlDecode(value[1..^1].Trim()), null);
        }

        var titleMatch = Regex.Match(value, "^(?<target>.+?)\\s+(?<quote>[\"'])(?<title>.*?)\\k<quote>\\s*$", RegexOptions.CultureInvariant);
        if (titleMatch.Success)
        {
            return (
                WebUtility.HtmlDecode(titleMatch.Groups["target"].Value.Trim()),
                WebUtility.HtmlDecode(titleMatch.Groups["title"].Value));
        }

        return (WebUtility.HtmlDecode(value), null);
    }

    private static string CreateImagePlaceholder(int index)
    {
        return $"MDV_IMAGE_PLACEHOLDER_{index}_END";
    }

    private static string ExtractMathSegments(string markdown, out List<MathSegment> segments)
    {
        segments = new List<MathSegment>();
        var builder = new StringBuilder(markdown.Length);
        var inFence = false;
        var inInlineCode = false;

        for (var i = 0; i < markdown.Length;)
        {
            if (IsAtLineStart(markdown, i) && StartsWithAt(markdown, i, "```"))
            {
                inFence = !inFence;
                builder.Append("```");
                i += 3;
                continue;
            }

            if (!inFence && markdown[i] == '`')
            {
                inInlineCode = !inInlineCode;
                builder.Append(markdown[i]);
                i++;
                continue;
            }

            if (!inFence && !inInlineCode && StartsWithAt(markdown, i, "$$"))
            {
                var end = markdown.IndexOf("$$", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    var source = markdown.Substring(i, end - i + 2);
                    var placeholder = CreateMathPlaceholder(segments.Count);
                    segments.Add(new MathSegment(placeholder, source, true));
                    builder.Append("\n\n").Append(placeholder).Append("\n\n");
                    i = end + 2;
                    continue;
                }
            }

            if (!inFence && !inInlineCode && markdown[i] == '$')
            {
                var end = FindInlineMathEnd(markdown, i + 1);
                if (end > i + 1)
                {
                    var source = markdown.Substring(i, end - i + 1);
                    var placeholder = CreateMathPlaceholder(segments.Count);
                    segments.Add(new MathSegment(placeholder, source, false));
                    builder.Append(placeholder);
                    i = end + 1;
                    continue;
                }
            }

            builder.Append(markdown[i]);
            i++;
        }

        return builder.ToString();
    }

    private static int FindInlineMathEnd(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\r' || text[i] == '\n')
            {
                return -1;
            }

            if (text[i] == '$' && text[i - 1] != '\\')
            {
                return i;
            }
        }

        return -1;
    }

    private static bool StartsWithAt(string text, int index, string value)
    {
        return index + value.Length <= text.Length
            && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;
    }

    private static bool IsAtLineStart(string text, int index)
    {
        return index == 0 || text[index - 1] == '\n' || text[index - 1] == '\r';
    }

    private static string CreateMathPlaceholder(int index)
    {
        return $"MDV_MATH_PLACEHOLDER_{index}_END";
    }

    private string BuildWysiwygDocument()
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        return $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'self' https://{{DocumentHost}} https://{{AssetHost}} data: blob:; img-src 'self' https://{{DocumentHost}} https://{{AssetHost}} https: http: data: file: blob:; script-src 'nonce-{{nonce}}' https://{{AssetHost}} 'unsafe-eval'; style-src 'unsafe-inline' https://{{AssetHost}}; font-src data: https://{{AssetHost}};">
<base href="https://{{DocumentHost}}/">
<link rel="stylesheet" href="https://{{AssetHost}}/highlight-github.min.css">
<link rel="stylesheet" href="https://{{AssetHost}}/katex/katex.min.css">
<style>
:root {
  --page: {{_colorProfile.Page}};
  --text: {{_colorProfile.Text}};
  --muted: {{_colorProfile.Muted}};
  --line: {{_colorProfile.Line}};
  --code: {{_colorProfile.Code}};
  --accent: {{_colorProfile.Accent}};
  --surface: {{_colorProfile.Surface}};
  --quote-bg: {{_colorProfile.QuoteBackground}};
  --heading: {{_colorProfile.Heading}};
}
html, body {
  margin: 0;
  min-height: 100%;
  max-width: 100%;
  overflow-x: hidden;
  overflow-y: auto;
  overscroll-behavior-x: none;
  background: transparent;
  color: var(--text);
  font: {{ContentFontSizeCss}}px/1.62 {{ContentFontCss}};
}
#editor {
  box-sizing: border-box;
  width: 100%;
  max-width: 920px;
  overflow-x: hidden;
  overscroll-behavior-x: none;
  margin: 0 auto;
  padding: 28px 36px 60px;
}
.md-block {
  border-radius: 6px;
  margin: 0 0 12px;
  padding: 3px 6px;
  outline: none;
}
.md-block:hover {
  background: rgba(23, 105, 170, .06);
}
.md-block.editing {
  background: rgba(23, 105, 170, .09);
  box-shadow: inset 0 0 0 1px rgba(23, 105, 170, .22);
}
.md-block.code {
  padding-left: 0;
  padding-right: 0;
}
.md-rendered > :first-child { margin-top: 0; }
.md-rendered > :last-child { margin-bottom: 0; }
.md-rendered h1,
.md-rendered h2,
.md-rendered h3 {
  line-height: 1.25;
  margin: 1.2em 0 .55em;
  color: var(--heading);
}
.md-rendered h1 {
  padding-bottom: .32em;
  border-bottom: 1px solid var(--line);
  font-size: 2.05em;
}
.md-rendered h2 {
  padding-bottom: .25em;
  border-bottom: 1px solid var(--line);
  font-size: 1.5em;
}
.md-rendered h3 { font-size: 1.22em; }
.md-rendered blockquote {
  border-left: 4px solid #9fb2c7;
  color: var(--muted);
  padding: .1em 1em;
  margin-left: 0;
  background: var(--quote-bg);
}
.md-rendered code,
.raw-editor {
  font-family: {{EditorFontCss}};
}
.md-rendered code {
  background: var(--code);
  border-radius: 4px;
  padding: .13em .32em;
  font-size: .92em;
}
.md-rendered pre {
  overflow: auto;
  overscroll-behavior-x: contain;
  background: var(--code);
  border: 0;
  border-radius: 0;
  padding: 14px 16px;
}
.md-rendered table {
  border-collapse: collapse;
  display: block;
  overflow-x: auto;
  max-width: 100%;
  overscroll-behavior-x: contain;
}
.md-rendered li.mdv-task-item {
  list-style-type: none;
}
.md-rendered li.mdv-task-item > input[type="checkbox"] {
  cursor: pointer;
  margin-left: -1.35em;
}
.md-rendered th,
.md-rendered td {
  border: 1px solid var(--line);
  padding: 6px 10px;
}
.md-rendered img {
  max-width: 100%;
  height: auto;
}
.mdv-front-matter {
  box-sizing: border-box;
  max-width: 100%;
  overflow: hidden;
  color: var(--muted);
  background: var(--quote-bg);
  border: 1px solid var(--line);
  border-radius: 6px;
  padding: 10px 12px;
  margin-bottom: 1em;
}
.mdv-front-matter-title {
  color: var(--heading);
  font-weight: 600;
  margin-bottom: 6px;
}
.mdv-front-matter table {
  width: 100%;
  max-width: 100%;
  table-layout: fixed;
  border-collapse: collapse;
  display: table;
  margin: 0;
}
.mdv-front-matter th {
  width: 11rem;
  max-width: 38%;
  white-space: normal;
  text-align: left;
  vertical-align: top;
}
.mdv-front-matter th,
.mdv-front-matter td {
  min-width: 0;
  overflow-wrap: anywhere;
  word-break: break-word;
  vertical-align: top;
}
.md-rendered .math-row {
  box-sizing: border-box;
  display: block;
  overflow: visible;
  width: 100%;
  margin: 1.1em 0;
  padding: 2px 0;
  text-align: center;
}
.md-rendered .math-shell {
  box-sizing: border-box;
  display: block;
  width: 100%;
  text-align: center;
}
.md-rendered .math-display {
  box-sizing: border-box;
  display: block;
  width: 100%;
  max-width: 100%;
  min-width: 0;
  margin: 1.1em 0;
  padding: 0;
  text-align: center;
}
.md-rendered .math-display .katex-display {
  display: block;
  width: 100%;
  margin: 0 auto !important;
  max-width: 100%;
  min-width: 0;
  text-align: center;
}
.md-rendered .math-display .katex {
  display: inline-block !important;
  text-align: center !important;
}
.md-block.math {
  box-sizing: border-box;
  display: block;
  width: 100%;
}
.md-block.math .md-rendered {
  box-sizing: border-box;
  display: block;
  text-align: center;
  width: 100%;
}
.md-block.math .math-display {
  width: 100%;
}
.md-block.math .math-row {
  width: 100%;
}
.md-block.math .math-shell {
  width: 100%;
  text-align: center;
}
.md-block.math .katex-display {
  width: 100% !important;
  text-align: center !important;
}
.md-block.math .katex-display > .katex {
  display: inline-block !important;
  width: auto !important;
  max-width: 100%;
  text-align: center !important;
}
.md-block.math .katex-display > .katex > .katex-html {
  display: inline-block !important;
  position: relative;
  text-align: left;
}
.md-rendered .math-inline {
  white-space: nowrap;
}
.md-rendered .math {
  visibility: hidden;
}
.md-rendered .math.math-ready {
  visibility: visible;
}
.raw-editor {
  box-sizing: border-box;
  display: block;
  width: 100%;
  min-height: 2.4em;
  resize: none;
  overflow: hidden;
  border: 0;
  outline: 0;
  background: transparent;
  color: var(--text);
  line-height: 1.45;
  padding: 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  word-break: break-word;
}
.raw-editor.h1 {
  font: 700 2.05em/1.25 "Segoe UI", system-ui, sans-serif;
  border-bottom: 1px solid var(--line);
}
.raw-editor.h2 {
  font: 700 1.5em/1.25 "Segoe UI", system-ui, sans-serif;
  border-bottom: 1px solid var(--line);
}
.raw-editor.h3 {
  font: 700 1.22em/1.25 "Segoe UI", system-ui, sans-serif;
}
.raw-editor.quote {
  color: var(--muted);
  font-style: italic;
  border-left: 4px solid #9fb2c7;
  padding-left: 12px;
}
.raw-editor.frontmatter {
  color: var(--muted);
  background: var(--quote-bg);
  border: 1px solid var(--line);
  border-radius: 6px;
  padding: 10px 12px;
}
.raw-editor.code {
  background: var(--code);
  border: 1px solid var(--line);
  border-radius: 6px;
  padding: 12px 14px;
}
.raw-editor.math {
  color: #9d174d;
  font-size: 1.08em;
}
.render-buffer {
  position: absolute;
  left: -100000px;
  top: 0;
  visibility: hidden;
  pointer-events: none;
}
.code-editor {
  overflow: hidden;
  border: 0;
  border-radius: 0;
  background: var(--code);
  box-shadow: none;
}
.code-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 34px;
  padding: 5px 8px;
  background: var(--quote-bg);
}
.code-language {
  width: 140px;
  border: 1px solid var(--line);
  border-radius: 4px;
  background: var(--surface);
  color: var(--text);
  font: 13px/1.4 "Segoe UI", system-ui, sans-serif;
  padding: 3px 7px;
  outline: none;
}
.code-body {
  box-sizing: border-box;
  display: block;
  width: 100%;
  min-height: 150px;
  resize: vertical;
  border: 0;
  outline: 0;
  background: transparent;
  color: var(--text);
  font: {{EditorFontSizeCss}}px/1.55 {{EditorFontCss}};
  padding: 13px 15px;
  tab-size: 4;
  white-space: pre;
}
.code-view {
  position: relative;
}
.code-language-badge {
  position: absolute;
  top: 7px;
  right: 9px;
  z-index: 1;
  color: #64748b;
  background: rgba(246, 248, 250, .9);
  border: 1px solid #d9e0e8;
  border-radius: 4px;
  font: 12px/1.2 "Segoe UI", system-ui, sans-serif;
  padding: 2px 6px;
}
.empty {
  color: #718096;
}
.slash-menu {
  position: fixed;
  z-index: 10;
  min-width: 220px;
  overflow: hidden;
  border: 1px solid #c7d0dc;
  border-radius: 8px;
  background: rgba(255, 255, 255, .98);
  box-shadow: 0 12px 32px rgba(15, 23, 42, .16);
}
.slash-item {
  padding: 8px 11px;
  cursor: default;
  color: #182433;
}
.slash-item small {
  display: block;
  color: #6b7280;
}
.slash-item:hover,
.slash-item.active {
  background: #e8f2ff;
}
</style>
</head>
<body>
<div id="editor"></div>
<script nonce="{{nonce}}">
let blocks = [];
let activeIndex = -1;
let inputTimer = 0;
let slashMenu = null;
let slashIndex = 0;
let wysiwygRenderToken = 0;
const commands = [
  { label: 'Heading 1', hint: '# Title', template: '# ' },
  { label: 'Heading 2', hint: '## Title', template: '## ' },
  { label: 'Heading 3', hint: '### Title', template: '### ' },
  { label: 'Bullet list', hint: '- item', template: '- ' },
  { label: 'Task item', hint: '- [ ] task', template: '- [ ] ' },
  { label: 'Quote', hint: '> quote', template: '> ' },
  { label: 'Code block', hint: '```language', template: '```csharp\n\n```', caret: 10 },
  { label: 'Display math', hint: '$$...$$', template: '$$\n\\alpha\n$$', caret: 3 },
  { label: 'Table', hint: '| Column | Value |', template: '| Column | Value |\n| --- | --- |\n|  |  |', caret: 28 }
];

function markdownText() {
  return persistedBlocks().map(function (block) {
    return (block.source || '').replace(/\s+$/g, '');
  }).join('\n\n');
}

function persistedBlocks() {
  const saved = blocks.slice();
  while (saved.length && !(saved[saved.length - 1].source || '').trim()) {
    saved.pop();
  }
  return saved;
}

function ensureTrailingEmptyBlock() {
  while (blocks.length > 1
    && !(blocks[blocks.length - 1].source || '').trim()
    && !(blocks[blocks.length - 2].source || '').trim()) {
    blocks.pop();
  }

  if (!blocks.length || (blocks[blocks.length - 1].source || '').trim()) {
    blocks.push({ index: blocks.length, source: '', html: '', kind: 'paragraph' });
  }

  blocks.forEach(function (block, index) {
    block.index = index;
  });
}

function post(type) {
  if (!window.chrome || !window.chrome.webview) return;
  window.chrome.webview.postMessage({ type: type, markdown: markdownText() });
}

document.addEventListener('keydown', function (event) {
  const key = (event.key || '').toLowerCase();
  const command = event.ctrlKey || event.metaKey;
  if (!command || event.altKey) return;
  if (key === 'z' && !event.shiftKey) {
    event.preventDefault();
    event.stopPropagation();
    post('undo');
  } else if (key === 'y' || (key === 'z' && event.shiftKey)) {
    event.preventDefault();
    event.stopPropagation();
    post('redo');
  }
}, true);

function debouncedInputPost() {
  clearTimeout(inputTimer);
  inputTimer = setTimeout(function () { post('input'); }, 120);
}

async function refreshEnhancements(scope) {
  const root = scope || document;
  normalizeDisplayMath(root);
  if (window.hljs) {
    root.querySelectorAll('pre code').forEach(function (block) {
      block.removeAttribute('data-highlighted');
      window.hljs.highlightElement(block);
    });
  }
  const mathNodes = Array.from(root.querySelectorAll ? root.querySelectorAll('.math') : []);
  mathNodes.forEach(function (node) {
    const source = mathSource(node);
    node.classList.remove('math-ready');
    if (window.katex) {
      window.katex.render(source, node, {
        displayMode: node.classList.contains('math-display'),
        throwOnError: false,
        output: 'htmlAndMathml'
      });
    }
    node.classList.add('math-ready');
  });
  centerDisplayMath(root);
}

function mathSource(node) {
  let source = (node.textContent || '').trim();
  if (source.startsWith('$$') && source.endsWith('$$')) {
    return source.slice(2, -2).trim();
  }
  if (source.startsWith('$') && source.endsWith('$')) {
    return source.slice(1, -1).trim();
  }
  return source;
}

function normalizeDisplayMath(root) {
  return root;
}

function centerDisplayMath(root) {
  const displays = Array.from(root.querySelectorAll ? root.querySelectorAll('.math-display') : []);
  displays.forEach(function (display) {
    display.style.display = 'block';
    display.style.width = '100%';
    display.style.textAlign = 'center';
    const rendered = display.closest('.md-rendered');
    if (rendered) {
      rendered.style.width = '100%';
      rendered.style.textAlign = 'center';
    }
    const katexDisplay = display.querySelector('.katex-display');
    if (katexDisplay) {
      katexDisplay.style.width = '100%';
      katexDisplay.style.textAlign = 'center';
      katexDisplay.style.marginLeft = 'auto';
      katexDisplay.style.marginRight = 'auto';
    }
    const katex = display.querySelector('.katex-display > .katex');
    if (katex) {
      katex.style.display = 'inline-block';
      katex.style.textAlign = 'initial';
      katex.style.maxWidth = '100%';
    }
    const katexHtml = display.querySelector('.katex-display > .katex > .katex-html');
    if (katexHtml) {
      katexHtml.style.display = 'inline-block';
      katexHtml.style.position = 'relative';
      katexHtml.style.textAlign = 'left';
    }
  });
}

function escapeHtml(value) {
  return (value || '').replace(/[&<>"']/g, function (ch) {
    return ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch];
  });
}

function escapeAttribute(value) {
  return escapeHtml(value || '');
}

function resolveImageSource(source) {
  const raw = (source || '').trim();
  if (!raw || /^(https?:|data:|file:|blob:)/i.test(raw)) return raw;
  if (/^[a-zA-Z]:[\\/]/.test(raw)) {
    return 'file:///' + raw.replace(/\\/g, '/').replace(/^([a-zA-Z]):/, '$1:').split('/').map(function (part, index) {
      return index === 0 ? part : encodeURIComponent(part);
    }).join('/');
  }
  if (/^\\\\/.test(raw)) {
    return 'file:' + raw.replace(/\\/g, '/').split('/').map(function (part, index) {
      return index < 2 ? part : encodeURIComponent(part);
    }).join('/');
  }
  try {
    return new URL(raw.replace(/\\/g, '/'), document.baseURI).href;
  } catch (_) {
    return raw.replace(/\\/g, '/').replace(/ /g, '%20');
  }
}

function renderInline(source) {
  const stash = [];
  function keep(html) {
    const key = '\u0000' + stash.length + '\u0000';
    stash.push(html);
    return key;
  }

  let text = escapeHtml(source || '');
  text = text.replace(/`([^`]+)`/g, function (_, code) {
    return keep('<code>' + code + '</code>');
  });
  text = text.replace(/\$\$([\s\S]+?)\$\$/g, function (match) {
    return keep('<span class="math math-inline">' + escapeHtml(match) + '</span>');
  });
  text = text.replace(/\$([^$\n]+?)\$/g, function (match) {
    return keep('<span class="math math-inline">' + escapeHtml(match) + '</span>');
  });
  text = text.replace(/!\[([^\]]*)\]\(([^)]+)\)/g, function (_, alt, target) {
    const src = target.trim().replace(/^<(.+)>$/, '$1').replace(/^(.+?)\s+["'][^"']+["']$/, '$1');
    return '<img alt="' + escapeAttribute(alt) + '" src="' + escapeAttribute(resolveImageSource(src)) + '">';
  });
  text = text.replace(/\[([^\]]+)\]\(([^)]+)\)/g, function (_, label, target) {
    return '<a href="' + escapeAttribute(target.trim()) + '">' + label + '</a>';
  });
  text = text.replace(/(\*\*|__)(.+?)\1/g, '<strong>$2</strong>');
  text = text.replace(/(\*|_)([^*_]+?)\1/g, '<em>$2</em>');
  stash.forEach(function (html, index) {
    text = text.replace('\u0000' + index + '\u0000', html);
  });
  return text;
}

function isFrontMatterBlock(source) {
  const lines = (source || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
  return lines.length >= 3 && lines[0].trim() === '---' && lines[lines.length - 1].trim() === '---';
}

function renderFrontMatter(source) {
  const lines = (source || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n').slice(1, -1);
  const rows = lines.filter(function (line) { return line.trim(); }).map(function (line) {
    const split = line.indexOf(':');
    if (split > 0) {
      return '<tr><th>' + escapeHtml(line.slice(0, split).trim()) + '</th><td>' + escapeHtml(line.slice(split + 1).trim()) + '</td></tr>';
    }
    return '<tr><td colspan="2">' + escapeHtml(line) + '</td></tr>';
  }).join('');
  return '<section class="mdv-front-matter"><div class="mdv-front-matter-title">Front matter</div><table>' + rows + '</table></section>';
}

function renderMarkdownBlock(source) {
  const raw = source || '';
  const trimmed = raw.trim();
  const lines = raw.replace(/\r\n/g, '\n').split('\n');

  if (!trimmed) return '<p class="empty">Click to write Markdown.</p>';

  if (isFrontMatterBlock(trimmed)) {
    return renderFrontMatter(trimmed);
  }

  const fence = trimmed.match(/^```([^\n]*)\n?([\s\S]*?)```$/);
  if (fence) {
    const lang = escapeHtml((fence[1] || '').trim());
    return '<pre><code class="language-' + lang + '">' + escapeHtml(fence[2] || '') + '</code></pre>';
  }

  if (/^\$\$[\s\S]*\$\$$/.test(trimmed)) {
    return '<div class="math math-display">' + escapeHtml(trimmed) + '</div>';
  }

  const heading = trimmed.match(/^(#{1,6})\s+(.+)$/);
  if (heading) {
    const level = Math.min(6, heading[1].length);
    return '<h' + level + '>' + renderInline(heading[2]) + '</h' + level + '>';
  }

  if (lines.every(function (line) { return /^\s*>/.test(line); })) {
    const body = lines.map(function (line) { return line.replace(/^\s*>\s?/, ''); }).join('\n');
    return '<blockquote><p>' + renderInline(body).replace(/\n/g, '<br>') + '</p></blockquote>';
  }

  if (lines.length >= 2 && /^\s*\|.+\|\s*$/.test(lines[0]) && /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$/.test(lines[1])) {
    const rows = lines.filter(function (line, index) { return index !== 1 && line.includes('|'); });
    const htmlRows = rows.map(function (line, index) {
      const cells = line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|');
      const tag = index === 0 ? 'th' : 'td';
      return '<tr>' + cells.map(function (cell) { return '<' + tag + '>' + renderInline(cell.trim()) + '</' + tag + '>'; }).join('') + '</tr>';
    }).join('');
    return '<table>' + htmlRows + '</table>';
  }

  if (lines.every(function (line) { return /^\s*([-+*]|\d+\.)\s+/.test(line); })) {
    const ordered = lines.every(function (line) { return /^\s*\d+\.\s+/.test(line); });
    const tag = ordered ? 'ol' : 'ul';
    const items = lines.map(function (line) {
      const text = line.replace(/^\s*([-+*]|\d+\.)\s+/, '');
      const task = text.match(/^\[( |x|X)\]\s+(.*)$/);
      if (task) {
        return '<li><input type="checkbox" disabled ' + (task[1].toLowerCase() === 'x' ? 'checked' : '') + '> ' + renderInline(task[2]) + '</li>';
      }
      return '<li>' + renderInline(text) + '</li>';
    }).join('');
    return '<' + tag + '>' + items + '</' + tag + '>';
  }

  return '<p>' + renderInline(raw).replace(/\n/g, '<br>') + '</p>';
}

function renderBlockInto(section, block, enhance) {
  if (enhance === undefined) enhance = true;
  updateBlockKind(block);
  section.className = 'md-block ' + block.kind;
  section.dataset.index = blocks.indexOf(block);
  const rendered = document.createElement('div');
  rendered.className = 'md-rendered';
  rendered.innerHTML = block.html || renderMarkdownBlock(block.source);
  decorateCodeBlocks(rendered);
  wireTaskCheckboxes(rendered, block, section);
  section.replaceChildren(rendered);
  if (enhance) refreshEnhancements(section);
}

function decorateCodeBlocks(root) {
  root.querySelectorAll('pre').forEach(function (pre) {
    if (pre.parentElement && pre.parentElement.classList.contains('code-view')) return;
    const code = pre.querySelector('code');
    const language = code ? Array.from(code.classList).map(function (name) {
      return name.startsWith('language-') ? name.substring(9) : '';
    }).find(Boolean) : '';
    const wrapper = document.createElement('div');
    wrapper.className = 'code-view';
    pre.replaceWith(wrapper);
    if (language) {
      const badge = document.createElement('span');
      badge.className = 'code-language-badge';
      badge.textContent = language;
      wrapper.appendChild(badge);
    }
    wrapper.appendChild(pre);
  });
}

function wireTaskCheckboxes(root, block, section) {
  const boxes = Array.from(root.querySelectorAll('input[type="checkbox"]'));
  boxes.forEach(function (box, taskIndex) {
    const item = box.closest('li');
    if (item) item.classList.add('mdv-task-item');
    box.disabled = false;
    box.addEventListener('click', function (event) {
      event.stopPropagation();
    });
    box.addEventListener('change', function () {
      toggleTaskSource(block, taskIndex, box.checked);
      renderBlockInto(section, block);
      post('blur');
    });
  });
}

function toggleTaskSource(block, taskIndex, checked) {
  let seen = 0;
  const lines = (block.source || '').replace(/\r\n/g, '\n').split('\n');
  for (let i = 0; i < lines.length; i++) {
    const match = lines[i].match(/^(\s*[-+*]\s+\[)( |x|X)(\]\s+.*)$/);
    if (!match) continue;
    if (seen === taskIndex) {
      lines[i] = match[1] + (checked ? 'x' : ' ') + match[3];
      block.source = lines.join('\n');
      block.html = '';
      updateBlockKind(block);
      return;
    }
    seen++;
  }
}

function renderAll(target, enhance) {
  const root = target || document.getElementById('editor');
  if (enhance === undefined) enhance = true;
  ensureTrailingEmptyBlock();
  root.innerHTML = '';
  blocks.forEach(function (block, index) {
    const section = document.createElement('section');
    section.tabIndex = 0;
    section.dataset.index = index;

    section.addEventListener('click', function (event) {
      if (event.target.closest('a')) return;
      if (event.target.closest('input[type="checkbox"]')) return;
      beginEdit(index);
    });
    section.addEventListener('focus', function () { beginEdit(index); });
    root.appendChild(section);
    renderBlockInto(section, block, enhance);
  });
}

function autosize(textarea) {
  textarea.style.height = 'auto';
  textarea.style.height = Math.max(42, textarea.scrollHeight) + 'px';
}

function setTextareaCaret(textarea, placement) {
  const value = textarea.value || '';
  const offset = placement === 'start' ? 0 : value.length;
  textarea.selectionStart = textarea.selectionEnd = offset;
}

function noArrowModifiers(event) {
  return !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey;
}

function isCaretOnFirstLine(textarea) {
  return textarea.value.lastIndexOf('\n', Math.max(0, textarea.selectionStart - 1)) < 0;
}

function isCaretOnLastLine(textarea) {
  return textarea.value.indexOf('\n', textarea.selectionEnd) < 0;
}

function moveBlockFocus(currentIndex, targetIndex, placement) {
  if (targetIndex < 0 || targetIndex >= blocks.length) return false;
  activeIndex = -1;
  removeSlashMenu();
  ensureTrailingEmptyBlock();
  renderAll();
  post('blur');
  setTimeout(function () { beginEdit(targetIndex, placement); }, 0);
  return true;
}

function handleBlockArrowNavigation(textarea, index, event, syncBeforeMove) {
  if (!noArrowModifiers(event) || textarea.selectionStart !== textarea.selectionEnd) {
    return false;
  }

  let targetIndex = -1;
  let placement = 'end';
  if (event.key === 'ArrowUp' && isCaretOnFirstLine(textarea)) {
    targetIndex = index - 1;
    placement = 'end';
  } else if (event.key === 'ArrowDown' && isCaretOnLastLine(textarea)) {
    targetIndex = index + 1;
    placement = 'start';
  } else if (event.key === 'ArrowLeft' && textarea.selectionStart === 0) {
    targetIndex = index - 1;
    placement = 'end';
  } else if (event.key === 'ArrowRight' && textarea.selectionEnd === textarea.value.length) {
    targetIndex = index + 1;
    placement = 'start';
  } else {
    return false;
  }

  if (targetIndex < 0 || targetIndex >= blocks.length) {
    return false;
  }

  event.preventDefault();
  if (syncBeforeMove) syncBeforeMove();
  return moveBlockFocus(index, targetIndex, placement);
}

function updateBlockKind(block) {
  const source = (block.source || '').trimStart();
  block.kind =
    source.startsWith('# ') ? 'h1' :
    source.startsWith('## ') ? 'h2' :
    source.startsWith('### ') ? 'h3' :
    source.startsWith('```') ? 'code' :
    isFrontMatterBlock(source.trim()) ? 'frontmatter' :
    source.startsWith('$$') ? 'math' :
    source.startsWith('>') ? 'quote' :
    /^([-+*]|\d+\.)\s+/.test(source) ? 'list' :
    'paragraph';
}

function insertAroundSelection(textarea, left, right) {
  const start = textarea.selectionStart;
  const end = textarea.selectionEnd;
  textarea.value = textarea.value.substring(0, start) + left + textarea.value.substring(start, end) + right + textarea.value.substring(end);
  textarea.selectionStart = start + left.length;
  textarea.selectionEnd = end + left.length;
  textarea.dispatchEvent(new Event('input'));
}

function pairKey(textarea, event) {
  const pairs = { '$': '$', '`': '`', '[': ']', '(': ')', '"': '"', "'": "'" };
  const right = pairs[event.key];
  if (!right) return false;
  event.preventDefault();
  insertAroundSelection(textarea, event.key, right);
  return true;
}

function getLineState(value, caret) {
  const lineStart = value.lastIndexOf('\n', Math.max(0, caret - 1)) + 1;
  let lineEnd = value.indexOf('\n', caret);
  if (lineEnd < 0) lineEnd = value.length;
  const line = value.substring(lineStart, lineEnd);
  const localCaret = caret - lineStart;
  return {
    lineStart: lineStart,
    lineEnd: lineEnd,
    before: line.substring(0, localCaret),
    after: line.substring(localCaret)
  };
}

function replaceTextareaRange(textarea, start, end, replacement, caretOffset) {
  textarea.value = textarea.value.substring(0, start) + replacement + textarea.value.substring(end);
  textarea.selectionStart = textarea.selectionEnd = start + caretOffset;
  textarea.dispatchEvent(new Event('input'));
}

function outdentIndent(indent) {
  if (!indent) return '';
  if (indent.endsWith('\t')) return indent.slice(0, -1);
  return indent.length <= 4 ? '' : indent.slice(0, -4);
}

function changeIndent(indent, outdent) {
  return outdent ? outdentIndent(indent) : indent + '    ';
}

function previousOrderedNumber(value, lineStart, indent, delimiter) {
  const lines = value.substring(0, lineStart).replace(/\r\n/g, '\n').split('\n');
  for (let i = lines.length - 1; i >= 0; i--) {
    const match = lines[i].match(/^(\s*)(\d+)([.)])\s+/);
    if (match && match[1] === indent && match[3] === delimiter) {
      return parseInt(match[2], 10) || 0;
    }
  }

  return 0;
}

function inferContinuation(value, lineStart, indent) {
  const lines = value.substring(0, lineStart).replace(/\r\n/g, '\n').split('\n');
  for (let i = lines.length - 1; i >= 0; i--) {
    let match = lines[i].match(/^(\s*)([-+*])\s+\[( |x|X)\]\s+/);
    if (match && match[1] === indent) return indent + match[2] + ' [ ] ';

    match = lines[i].match(/^(\s*)(\d+)([.)])\s+/);
    if (match && match[1] === indent) return indent + String((parseInt(match[2], 10) || 0) + 1) + match[3] + ' ';

    match = lines[i].match(/^(\s*)([-+*])\s+/);
    if (match && match[1] === indent) return indent + match[2] + ' ';
  }

  return null;
}

function replaceListLine(textarea, state, oldPrefixLength, newPrefix, content) {
  const localCaret = textarea.selectionStart - state.lineStart;
  const replacement = newPrefix + content;
  const caretOffset = localCaret <= oldPrefixLength
    ? newPrefix.length
    : Math.min(replacement.length, newPrefix.length + localCaret - oldPrefixLength);
  replaceTextareaRange(textarea, state.lineStart, state.lineEnd, replacement, caretOffset);
}

function handleListTab(textarea, outdent) {
  const state = getLineState(textarea.value, textarea.selectionStart);
  const line = state.before + state.after;
  let prefix = line.match(/^(\s*)([-+*])\s+\[( |x|X)\]\s*/);
  if (prefix) {
    const indent = prefix[1];
    const newIndent = changeIndent(indent, outdent);
    if (newIndent === indent) return true;
    replaceListLine(textarea, state, prefix[0].length, newIndent + prefix[2] + ' [' + prefix[3] + '] ', line.slice(prefix[0].length));
    return true;
  }

  prefix = line.match(/^(\s*)(\d+)([.)])\s*/);
  if (prefix) {
    const indent = prefix[1];
    const delimiter = prefix[3];
    const newIndent = changeIndent(indent, outdent);
    if (newIndent === indent) return true;
    const number = outdent ? previousOrderedNumber(textarea.value, state.lineStart, newIndent, delimiter) + 1 : 1;
    replaceListLine(textarea, state, prefix[0].length, newIndent + number + delimiter + ' ', line.slice(prefix[0].length));
    return true;
  }

  prefix = line.match(/^(\s*)([-+*])\s*/);
  if (!prefix) return false;

  const indent = prefix[1];
  const newIndent = changeIndent(indent, outdent);
  if (newIndent === indent) return true;
  replaceListLine(textarea, state, prefix[0].length, newIndent + prefix[2] + ' ', line.slice(prefix[0].length));
  return true;
}

function handleListEnter(textarea) {
  const state = getLineState(textarea.value, textarea.selectionStart);
  const afterIsEmpty = state.after.trim().length === 0;
  if (state.before.length && /^\s+$/.test(state.before) && afterIsEmpty) {
    const parentIndent = outdentIndent(state.before);
    const replacement = inferContinuation(textarea.value, state.lineStart, parentIndent);
    if (replacement === null) return false;
    replaceTextareaRange(textarea, state.lineStart, state.lineEnd, replacement, replacement.length);
    return true;
  }

  let match = state.before.match(/^(\s*)([-+*])\s+\[( |x|X)\]\s+(.*)$/);
  if (match) {
    const indent = match[1];
    const marker = match[2];
    if (!match[4].trim() && afterIsEmpty) {
      replaceTextareaRange(textarea, state.lineStart, state.lineEnd, indent, indent.length);
      return true;
    }

    const continuation = '\n' + indent + marker + ' [ ] ';
    replaceTextareaRange(textarea, textarea.selectionStart, textarea.selectionEnd, continuation, continuation.length);
    return true;
  }

  match = state.before.match(/^(\s*)(\d+)([.)])\s+(.*)$/);
  if (match) {
    const indent = match[1];
    const next = String(parseInt(match[2], 10) + 1);
    const delimiter = match[3];
    if (!match[4].trim() && afterIsEmpty) {
      replaceTextareaRange(textarea, state.lineStart, state.lineEnd, indent, indent.length);
      return true;
    }

    const continuation = '\n' + indent + next + delimiter + ' ';
    replaceTextareaRange(textarea, textarea.selectionStart, textarea.selectionEnd, continuation, continuation.length);
    return true;
  }

  match = state.before.match(/^(\s*)([-+*])\s+(.*)$/);
  if (!match) return false;

  const indent = match[1];
  const marker = match[2];
  if (!match[3].trim() && afterIsEmpty) {
    replaceTextareaRange(textarea, state.lineStart, state.lineEnd, indent, indent.length);
    return true;
  }

  const continuation = '\n' + indent + marker + ' ';
  replaceTextareaRange(textarea, textarea.selectionStart, textarea.selectionEnd, continuation, continuation.length);
  return true;
}

function removeSlashMenu() {
  if (slashMenu) slashMenu.remove();
  slashMenu = null;
  slashIndex = 0;
}

function showSlashMenu(textarea, index) {
  const value = textarea.value.trim();
  if (!value.startsWith('/')) {
    removeSlashMenu();
    return;
  }

  if (!slashMenu) {
    slashMenu = document.createElement('div');
    slashMenu.className = 'slash-menu';
    document.body.appendChild(slashMenu);
  }

  slashMenu.innerHTML = '';
  commands.forEach(function (command, commandIndex) {
    const item = document.createElement('div');
    item.className = 'slash-item' + (commandIndex === slashIndex ? ' active' : '');
    item.innerHTML = command.label + '<small>' + command.hint + '</small>';
    item.addEventListener('mousedown', function (event) {
      event.preventDefault();
      applyCommand(textarea, index, commandIndex);
    });
    slashMenu.appendChild(item);
  });

  const rect = textarea.getBoundingClientRect();
  slashMenu.style.left = Math.max(12, rect.left) + 'px';
  slashMenu.style.top = (rect.bottom + 6) + 'px';
}

function applyCommand(textarea, index, commandIndex) {
  const command = commands[commandIndex] || commands[0];
  textarea.value = command.template;
  textarea.selectionStart = textarea.selectionEnd = command.caret || command.template.length;
  blocks[index].source = textarea.value;
  blocks[index].html = '';
  updateBlockKind(blocks[index]);
  if (blocks[index].kind === 'code') {
    const section = textarea.closest('.md-block');
    removeSlashMenu();
    debouncedInputPost();
    if (section) beginCodeEdit(index, section, blocks[index]);
    return;
  }

  textarea.className = 'raw-editor ' + blocks[index].kind;
  autosize(textarea);
  textarea.dispatchEvent(new Event('input'));
  removeSlashMenu();
}

function splitBlock(textarea, index) {
  const block = blocks[index];
  const start = textarea.selectionStart;
  const end = textarea.selectionEnd;
  const before = textarea.value.substring(0, start).replace(/\s+$/g, '');
  const after = textarea.value.substring(end).replace(/^\s+/g, '');
  block.source = before;
  block.html = '';
  updateBlockKind(block);
  blocks.splice(index + 1, 0, { index: index + 1, source: after, html: '', kind: 'paragraph' });
  activeIndex = -1;
  removeSlashMenu();
  renderAll();
  post('input');
  setTimeout(function () { beginEdit(index + 1); }, 0);
}

function parseCodeFence(source) {
  const raw = (source || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
  const match = raw.match(/^```([^\n`]*)\n?([\s\S]*?)\n?```$/);
  if (!match) {
    return { language: '', code: raw.replace(/^```/, '').replace(/```$/, '') };
  }

  return { language: (match[1] || '').trim(), code: (match[2] || '').replace(/\n$/, '') };
}

function formatCodeFence(language, code) {
  const cleanLanguage = (language || '').trim().replace(/\s+/g, '-');
  const cleanCode = (code || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
  return '```' + cleanLanguage + '\n' + cleanCode + '\n```';
}

function beginCodeEdit(index, section, block, placement) {
  const parsed = parseCodeFence(block.source);
  const shell = document.createElement('div');
  shell.className = 'code-editor';

  const toolbar = document.createElement('div');
  toolbar.className = 'code-toolbar';
  const language = document.createElement('input');
  language.className = 'code-language';
  language.spellcheck = false;
  language.value = parsed.language;
  language.setAttribute('aria-label', 'Language');
  toolbar.appendChild(language);

  const body = document.createElement('textarea');
  body.className = 'code-body';
  body.spellcheck = false;
  body.value = parsed.code;

  shell.appendChild(toolbar);
  shell.appendChild(body);
  section.replaceChildren(shell);

  function syncCode() {
    block.source = formatCodeFence(language.value, body.value);
    block.html = '';
    updateBlockKind(block);
    debouncedInputPost();
  }

  function closeEditor() {
    if (activeIndex !== index) return;
    syncCode();
    activeIndex = -1;
    removeSlashMenu();
    ensureTrailingEmptyBlock();
    renderAll();
    post('blur');
  }

  shell.mdvClose = closeEditor;
  language.addEventListener('input', syncCode);
  body.addEventListener('input', syncCode);
  shell.addEventListener('focusout', function () {
    setTimeout(function () {
      if (!shell.contains(document.activeElement)) closeEditor();
    }, 0);
  });

  language.addEventListener('keydown', function (event) {
    if (event.key === 'Enter') {
      event.preventDefault();
      body.focus();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      language.blur();
    }
  });

  body.addEventListener('keydown', function (event) {
    if (handleBlockArrowNavigation(body, index, event, syncCode)) {
      return;
    }

    if (event.key === 'Tab') {
      event.preventDefault();
      const start = body.selectionStart;
      const end = body.selectionEnd;
      body.value = body.value.substring(0, start) + '    ' + body.value.substring(end);
      body.selectionStart = body.selectionEnd = start + 4;
      syncCode();
    } else if (event.key === 'Escape' || (event.key === 'Enter' && event.ctrlKey)) {
      event.preventDefault();
      body.blur();
    }
  });

  body.focus();
  setTextareaCaret(body, placement || 'end');
}

function beginEdit(index, placement) {
  if (activeIndex === index) return;
  const existing = document.querySelector('.raw-editor');
  if (existing) existing.blur();
  const existingCode = document.querySelector('.code-editor');
  if (existingCode && existingCode.mdvClose) {
    existingCode.mdvClose();
  } else if (existingCode) {
    const focusedCodeField = existingCode.querySelector('textarea, input');
    if (focusedCodeField) focusedCodeField.blur();
  }

  activeIndex = index;
  const block = blocks[index];
  const section = document.querySelector('[data-index="' + index + '"]');
  if (!section) return;

  section.classList.add('editing');
  updateBlockKind(block);
  if (block.kind === 'code') {
    beginCodeEdit(index, section, block, placement);
    return;
  }

  const textarea = document.createElement('textarea');
  textarea.className = 'raw-editor ' + block.kind;
  textarea.value = block.source || '';
  section.replaceChildren(textarea);
  autosize(textarea);
  textarea.focus();
  setTextareaCaret(textarea, placement || 'end');

  textarea.addEventListener('input', function () {
    block.source = textarea.value;
    block.html = '';
    updateBlockKind(block);
    textarea.className = 'raw-editor ' + block.kind;
    autosize(textarea);
    showSlashMenu(textarea, index);
    debouncedInputPost();
  });

  textarea.addEventListener('keydown', function (event) {
    if (slashMenu && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
      event.preventDefault();
      slashIndex = event.key === 'ArrowDown'
        ? (slashIndex + 1) % commands.length
        : (slashIndex + commands.length - 1) % commands.length;
      showSlashMenu(textarea, index);
      return;
    }

    if (slashMenu && event.key === 'Enter') {
      event.preventDefault();
      applyCommand(textarea, index, slashIndex);
      return;
    }

    if (!slashMenu && handleBlockArrowNavigation(textarea, index, event, function () {
      block.source = textarea.value;
      block.html = '';
      updateBlockKind(block);
    })) {
      return;
    }

    if (event.key === 'Tab') {
      event.preventDefault();
      if (block.kind === 'list' && handleListTab(textarea, event.shiftKey)) {
        return;
      }

      const start = textarea.selectionStart;
      const end = textarea.selectionEnd;
      textarea.value = textarea.value.substring(0, start) + '    ' + textarea.value.substring(end);
      textarea.selectionStart = textarea.selectionEnd = start + 4;
      textarea.dispatchEvent(new Event('input'));
    } else if (event.ctrlKey && event.key.toLowerCase() === 'b') {
      event.preventDefault();
      insertAroundSelection(textarea, '**', '**');
    } else if (event.ctrlKey && event.key.toLowerCase() === 'i') {
      event.preventDefault();
      insertAroundSelection(textarea, '*', '*');
    } else if (pairKey(textarea, event)) {
      return;
    } else if (event.key === 'Enter' && !event.shiftKey && !event.ctrlKey) {
      if (handleListEnter(textarea)) {
        event.preventDefault();
        return;
      }

      if (block.kind !== 'code' && block.kind !== 'math') {
        event.preventDefault();
        splitBlock(textarea, index);
      }
    } else if (event.key === 'Escape' || (event.key === 'Enter' && event.ctrlKey)) {
      textarea.blur();
    }
  });

  textarea.addEventListener('blur', function () {
    removeSlashMenu();
    if (!document.body.contains(section)) return;
    block.source = textarea.value;
    block.html = '';
    updateBlockKind(block);
    activeIndex = -1;
    ensureTrailingEmptyBlock();
    renderAll();
    post('blur');
  });
}

window.mdvGetMarkdown = markdownText;
window.mdvInsertMarkdown = function (markdown) {
  const text = markdown || '';
  const textarea = document.querySelector('.raw-editor');
  if (textarea) {
    const index = activeIndex;
    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    const prefix = start > 0 && textarea.value[start - 1] !== '\n' ? '\n' : '';
    const suffix = end < textarea.value.length && textarea.value[end] !== '\n' ? '\n' : '';
    textarea.value = textarea.value.substring(0, start) + prefix + text + suffix + textarea.value.substring(end);
    const caret = start + prefix.length + text.length;
    textarea.selectionStart = textarea.selectionEnd = caret;
    if (index >= 0 && blocks[index]) {
      blocks[index].source = textarea.value;
      blocks[index].html = '';
      updateBlockKind(blocks[index]);
    }
    autosize(textarea);
    post('input');
    return true;
  }

  const insertIndex = Math.max(0, blocks.length - 1);
  blocks.splice(insertIndex, 0, { index: insertIndex, source: text, html: '', kind: 'paragraph' });
  activeIndex = -1;
  ensureTrailingEmptyBlock();
  renderAll();
  post('input');
  setTimeout(function () { beginEdit(insertIndex, 'end'); }, 0);
  return true;
};
window.mdvSetBlocks = async function (nextBlocks) {
  const token = ++wysiwygRenderToken;
  if (activeIndex >= 0) {
    return false;
  }

  blocks = Array.isArray(nextBlocks) && nextBlocks.length ? nextBlocks : [{ index: 0, source: '', html: '', kind: 'paragraph' }];
  const root = document.getElementById('editor');
  const scrollRatio = document.documentElement.scrollHeight <= window.innerHeight
    ? 0
    : window.scrollY / (document.documentElement.scrollHeight - window.innerHeight);
  const buffer = document.createElement('div');
  buffer.className = 'render-buffer';
  buffer.style.width = root.getBoundingClientRect().width + 'px';
  document.body.appendChild(buffer);

  try {
    renderAll(buffer, false);
    await refreshEnhancements(buffer);
    if (token !== wysiwygRenderToken || activeIndex >= 0) {
      buffer.remove();
      return false;
    }

    root.replaceChildren(...Array.from(buffer.childNodes));
    const maxScroll = Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
    window.scrollTo(0, Math.min(maxScroll, maxScroll * scrollRatio));
    return true;
  } finally {
    buffer.remove();
  }
};
</script>
<script src="https://{{AssetHost}}/highlight.min.js"></script>
<script src="https://{{AssetHost}}/katex/katex.min.js"></script>
<script nonce="{{nonce}}">
refreshEnhancements(document);
</script>
</body>
</html>
""";
    }

    private IReadOnlyList<WysiwygBlock> BuildWysiwygBlocks(string markdown)
    {
        var sources = SplitMarkdownBlocks(markdown);
        if (sources.Count == 0)
        {
            sources.Add(string.Empty);
        }

        return sources
            .Select((source, index) => new WysiwygBlock(index, source, RenderMarkdownToWysiwygHtml(source), GetBlockKind(source)))
            .ToArray();
    }

    private string RenderMarkdownToWysiwygHtml(string source)
    {
        return RenderMarkdownToHtml(source);
    }

    private static List<string> SplitMarkdownBlocks(string markdown)
    {
        return SplitMarkdownBlocksWithLines(markdown)
            .Select(block => block.Source)
            .ToList();
    }

    private static List<MarkdownSourceBlock> SplitMarkdownBlocksWithLines(string markdown)
    {
        var normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var blocks = new List<MarkdownSourceBlock>();
        var current = new List<string>();
        var currentStartLine = 1;

        void AddCurrentLine(string line, int lineNumber)
        {
            if (current.Count == 0)
            {
                currentStartLine = lineNumber;
            }

            current.Add(line);
        }

        void Flush(int endLine)
        {
            if (current.Count == 0)
            {
                return;
            }

            blocks.Add(new MarkdownSourceBlock(currentStartLine, Math.Max(currentStartLine, endLine), string.Join('\n', current).TrimEnd()));
            current.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (i == 0 && trimmed == "---")
            {
                var matter = new List<string> { line };
                i++;
                while (i < lines.Length)
                {
                    matter.Add(lines[i]);
                    if (i > 0 && lines[i].Trim() == "---")
                    {
                        break;
                    }
                    i++;
                }

                if (matter.Count >= 3 && matter[^1].Trim() == "---")
                {
                    blocks.Add(new MarkdownSourceBlock(1, matter.Count, string.Join('\n', matter).TrimEnd()));
                    continue;
                }

                i = 0;
            }

            if (trimmed.Length == 0)
            {
                Flush(i);
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Flush(i);
                var startLine = i + 1;
                var fenced = new List<string> { line };
                i++;
                while (i < lines.Length)
                {
                    fenced.Add(lines[i]);
                    if (lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                    {
                        break;
                    }
                    i++;
                }

                blocks.Add(new MarkdownSourceBlock(startLine, i + 1, string.Join('\n', fenced).TrimEnd()));
                continue;
            }

            if (trimmed.StartsWith("$$", StringComparison.Ordinal) && !trimmed.EndsWith("$$", StringComparison.Ordinal))
            {
                Flush(i);
                var startLine = i + 1;
                var math = new List<string> { line };
                i++;
                while (i < lines.Length)
                {
                    math.Add(lines[i]);
                    if (lines[i].Trim().EndsWith("$$", StringComparison.Ordinal))
                    {
                        break;
                    }
                    i++;
                }

                blocks.Add(new MarkdownSourceBlock(startLine, i + 1, string.Join('\n', math).TrimEnd()));
                continue;
            }

            AddCurrentLine(line, i + 1);
        }

        Flush(lines.Length);
        return blocks;
    }

    private static string GetBlockKind(string source)
    {
        var trimmed = source.TrimStart();
        if (trimmed.StartsWith("# ", StringComparison.Ordinal)) return "h1";
        if (trimmed.StartsWith("## ", StringComparison.Ordinal)) return "h2";
        if (trimmed.StartsWith("### ", StringComparison.Ordinal)) return "h3";
        if (trimmed.StartsWith("```", StringComparison.Ordinal)) return "code";
        if (IsFrontMatterBlock(source)) return "frontmatter";
        if (trimmed.StartsWith("$$", StringComparison.Ordinal)) return "math";
        if (trimmed.StartsWith(">", StringComparison.Ordinal)) return "quote";
        if (Regex.IsMatch(trimmed, @"^([-+*]|\d+\.)\s+")) return "list";
        return "paragraph";
    }

    private void MapCommonFolders(CoreWebView2 webView)
    {
        MapAssetFolder(webView);
        MapDocumentFolder(webView);
    }

    private void ConfigureDocumentResourceHandler(CoreWebView2 webView)
    {
        webView.AddWebResourceRequestedFilter(
            $"https://{DocumentHost}/*",
            CoreWebView2WebResourceContext.Image,
            CoreWebView2WebResourceRequestSourceKinds.All);
        webView.AddWebResourceRequestedFilter(
            $"https://{AssetHost}/*",
            CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        webView.WebResourceRequested += AppWebResourceRequested;
    }

    private void AppWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 webView
            || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
            || !uri.Host.Equals(DocumentHost, StringComparison.OrdinalIgnoreCase)
                && !uri.Host.Equals(AssetHost, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (uri.Host.Equals(AssetHost, StringComparison.OrdinalIgnoreCase))
        {
            if (TryCreateAssetResourceResponse(webView, uri, out var response))
            {
                e.Response = response;
            }

            return;
        }

        if (e.ResourceContext != CoreWebView2WebResourceContext.Image)
        {
            return;
        }

        var localPath = GetLocalPathFromPreviewUri(uri);
        if (localPath is null
            || !IsPathInsideFolder(localPath, GetDocumentBaseFolder())
            || !File.Exists(localPath)
            || !IsImageFile(localPath))
        {
            e.Response = CreateTextResourceResponse(webView, 404, "Not Found", "Image not found.");
            return;
        }

        try
        {
            var stream = new MemoryStream(File.ReadAllBytes(localPath));
            var headers = $"Content-Type: {GetImageMimeType(localPath)}\r\nCache-Control: no-cache\r\nAccess-Control-Allow-Origin: *";
            e.Response = webView.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
        }
        catch
        {
            e.Response = CreateTextResourceResponse(webView, 500, "Internal Server Error", "Could not read image.");
        }
    }

    private static bool TryCreateAssetResourceResponse(
        CoreWebView2 webView,
        Uri uri,
        out CoreWebView2WebResourceResponse? response)
    {
        response = null;
        var relativePath = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Split('/').Any(part => part is "." or ".."))
        {
            return false;
        }

        var key = "Assets/" + relativePath;
        if (!EmbeddedAssetNames.Value.TryGetValue(key, out var resourceName))
        {
            return false;
        }

        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return false;
        }

        var headers = $"Content-Type: {GetAssetMimeType(relativePath)}\r\nCache-Control: public, max-age=31536000";
        response = webView.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
        return true;
    }

    private static IReadOnlyDictionary<string, string> BuildEmbeddedAssetMap()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly
            .GetManifestResourceNames()
            .Select(name => new
            {
                Name = name,
                Key = NormalizeEmbeddedAssetName(name)
            })
            .Where(entry => entry.Key.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeEmbeddedAssetName(string resourceName)
    {
        var normalized = resourceName.Replace('\\', '/');
        var index = normalized.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return normalized[index..];
        }

        var marker = ".Assets.";
        index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0
            ? "Assets/" + normalized[(index + marker.Length)..].Replace('.', '/')
            : normalized;
    }

    private static CoreWebView2WebResourceResponse CreateTextResourceResponse(
        CoreWebView2 webView,
        int statusCode,
        string reason,
        string text)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return webView.Environment.CreateWebResourceResponse(
            stream,
            statusCode,
            reason,
            "Content-Type: text/plain; charset=utf-8\r\nCache-Control: no-cache");
    }

    private void MapAssetFolder(CoreWebView2 webView)
    {
        if (!Directory.Exists(_assetFolder))
        {
            return;
        }

        try
        {
            webView.ClearVirtualHostNameToFolderMapping(AssetHost);
        }
        catch
        {
        }

        webView.SetVirtualHostNameToFolderMapping(
            AssetHost,
            _assetFolder,
            CoreWebView2HostResourceAccessKind.Allow);
    }

    private void MapDocumentFolder(CoreWebView2 webView)
    {
        var folder = GetDocumentBaseFolder();
        try
        {
            webView.ClearVirtualHostNameToFolderMapping(DocumentHost);
        }
        catch
        {
        }

        webView.SetVirtualHostNameToFolderMapping(
            DocumentHost,
            folder,
            CoreWebView2HostResourceAccessKind.Allow);
    }

    private string GetDocumentBaseFolder()
    {
        if (_currentFilePath is not null)
        {
            var directory = Path.GetDirectoryName(_currentFilePath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                return directory;
            }
        }

        return Environment.CurrentDirectory;
    }

    private static bool IsPathInsideFolder(string path, string folder)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var folderPrefix = fullFolder + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void Preview_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (uri.Scheme is "about" or "data" || uri.Host.Equals(AssetHost, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (uri.Host.Equals(DocumentHost, StringComparison.OrdinalIgnoreCase))
        {
            var localPath = GetLocalPathFromPreviewUri(uri);
            if (localPath is null || !File.Exists(localPath))
            {
                if (!string.IsNullOrWhiteSpace(uri.Fragment) && sender is CoreWebView2 webView)
                {
                    e.Cancel = true;
                    ScrollPreviewToAnchor(webView, uri.Fragment);
                }

                return;
            }

            e.Cancel = true;
            if (IsMarkdownFile(localPath))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (ConfirmSaveChanges())
                    {
                        LoadFile(localPath);
                    }
                });
            }
            else
            {
                OpenExternal(localPath);
            }

            return;
        }

        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    private async void ScrollPreviewToAnchor(CoreWebView2 webView, string fragment)
    {
        var id = Uri.UnescapeDataString(fragment.TrimStart('#'));
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var jsonId = JsonSerializer.Serialize(id);
        var script = $"var el = document.getElementById({jsonId}); if (el) el.scrollIntoView({{behavior:'smooth', block:'start'}});";
        await webView.ExecuteScriptAsync(script);
    }

    private string? GetLocalPathFromPreviewUri(Uri uri)
    {
        var relative = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
            .Replace('/', Path.DirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        try
        {
            var baseFolder = GetDocumentBaseFolder();
            return Path.GetFullPath(Path.Combine(baseFolder, relative));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsMarkdownFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mdown", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mkd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageFile(string path)
    {
        return IsImageExtension(Path.GetExtension(path));
    }

    private static bool IsImageExtension(string extension)
    {
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        await ShowSystemPrintDialogAsync();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        await ShowSystemPrintDialogAsync();
    }

    private async Task ShowSystemPrintDialogAsync()
    {
        if (!_previewReady || Preview.CoreWebView2 is null)
        {
            StatusText.Text = "Preview is not ready.";
            return;
        }

        await SyncWysiwygEditorAsync();
        await RenderPreviewAsync();
        Preview.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System);
        StatusText.Text = "Choose Microsoft Print to PDF in the system print dialog to export PDF.";
    }

    private void ShowFindBar()
    {
        FindBar.Visibility = Visibility.Visible;
        if (string.IsNullOrEmpty(FindTextBox.Text) && Editor.SelectedText.Length > 0)
        {
            FindTextBox.Text = Editor.SelectedText;
        }

        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private void HideFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        if (_wysiwygMode)
        {
            Wysiwyg.Focus();
        }
        else
        {
            Editor.TextArea.Focus();
        }
    }

    private async Task FindAsync(bool backwards)
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            ShowFindBar();
            return;
        }

        if (_wysiwygMode && Wysiwyg.Visibility == Visibility.Visible)
        {
            await FindInWebViewAsync(Wysiwyg, query, backwards, "WYSIWYG editor");
            return;
        }

        if (_viewMode != ViewMode.PreviewOnly && (Editor.TextArea.IsKeyboardFocusWithin || _viewMode == ViewMode.EditorOnly))
        {
            FindInEditor(query, backwards);
            return;
        }

        if (_previewReady && Preview.Visibility == Visibility.Visible)
        {
            await FindInWebViewAsync(Preview, query, backwards, "preview");
            return;
        }

        FindInEditor(query, backwards);
    }

    private void FindInEditor(string query, bool backwards)
    {
        var text = Editor.Text;
        var comparison = StringComparison.CurrentCultureIgnoreCase;
        int index;

        if (backwards)
        {
            var start = Math.Max(0, Editor.SelectionStart - 1);
            index = text.LastIndexOf(query, start, comparison);
            if (index < 0)
            {
                index = text.LastIndexOf(query, comparison);
            }
        }
        else
        {
            var start = Editor.SelectionStart + Math.Max(1, Editor.SelectionLength);
            if (start > text.Length)
            {
                start = 0;
            }

            index = text.IndexOf(query, start, comparison);
            if (index < 0)
            {
                index = text.IndexOf(query, comparison);
            }
        }

        if (index < 0)
        {
            StatusText.Text = $"No matches for \"{query}\"";
            return;
        }

        Editor.TextArea.Focus();
        Editor.Select(index, query.Length);
        var line = Editor.Document.GetLineByOffset(index);
        Editor.ScrollToLine(line.LineNumber);
        StatusText.Text = $"Found \"{query}\" in editor";
    }

    private async Task FindInWebViewAsync(WpfWebView2 webView, string query, bool backwards, string targetName)
    {
        if (webView.CoreWebView2 is null)
        {
            return;
        }

        var search = JsonSerializer.Serialize(query);
        var direction = backwards ? "true" : "false";
        var script = $"window.find({search}, false, {direction}, true, false, false, false);";
        var result = await webView.ExecuteScriptAsync(script);
        StatusText.Text = result == "true" ? $"Found \"{query}\" in {targetName}" : $"No matches for \"{query}\"";
        webView.Focus();
    }

    private void ShowCompletionWindow(bool replaceActivationText)
    {
        if (_wysiwygMode || _viewMode == ViewMode.PreviewOnly)
        {
            return;
        }

        _completionWindow?.Close();
        _completionWindow = new CompletionWindow(Editor.TextArea);
        var replacement = replaceActivationText
            ? GetCompletionActivationSegment()
            : new TextSegment { StartOffset = Editor.CaretOffset, Length = 0 };

        _completionWindow.StartOffset = replacement.StartOffset;
        _completionWindow.EndOffset = replacement.StartOffset + replacement.Length;

        var data = _completionWindow.CompletionList.CompletionData;
        foreach (var item in CreateCompletionItems())
        {
            data.Add(item);
        }

        _completionWindow.Closed += (_, _) => _completionWindow = null;
        _completionWindow.Show();
    }

    private TextSegment GetCompletionActivationSegment()
    {
        var caret = Editor.CaretOffset;
        if (caret <= 0 || Editor.Document is null)
        {
            return new TextSegment { StartOffset = caret, Length = 0 };
        }

        var line = Editor.Document.GetLineByOffset(caret);
        var prefix = Editor.Document.GetText(line.Offset, caret - line.Offset);
        var heading = Regex.Match(prefix, @"(?<=^|\s)#{1,6}$");
        if (heading.Success)
        {
            return new TextSegment { StartOffset = line.Offset + heading.Index, Length = heading.Length };
        }

        var marker = Regex.Match(prefix, @"(?<=^|\s)([-+*>!]|\d+\.)$");
        if (marker.Success)
        {
            return new TextSegment { StartOffset = line.Offset + marker.Index, Length = marker.Length };
        }

        var inlineMarker = Regex.Match(prefix, @"(\$|`|\[)$");
        if (inlineMarker.Success)
        {
            return new TextSegment { StartOffset = line.Offset + inlineMarker.Index, Length = inlineMarker.Length };
        }

        return new TextSegment { StartOffset = caret, Length = 0 };
    }

    private static IEnumerable<MarkdownCompletionData> CreateCompletionItems()
    {
        yield return new MarkdownCompletionData("# Heading", "Level 1 heading", "# Heading", 2);
        yield return new MarkdownCompletionData("## Heading", "Level 2 heading", "## Heading", 3);
        yield return new MarkdownCompletionData("### Heading", "Level 3 heading", "### Heading", 4);
        yield return new MarkdownCompletionData("- List item", "Bullet list item", "- List item", 2);
        yield return new MarkdownCompletionData("1. List item", "Numbered list item", "1. List item", 3);
        yield return new MarkdownCompletionData("- [ ] Task", "Task list item", "- [ ] Task", 6);
        yield return new MarkdownCompletionData("> Quote", "Block quote", "> Quote", 2);
        yield return new MarkdownCompletionData("[text](url)", "Inline link", "[text](url)", 1);
        yield return new MarkdownCompletionData("![alt](path)", "Image", "![alt](path)", 2);
        yield return new MarkdownCompletionData("`code`", "Inline code", "`code`", 1);
        yield return new MarkdownCompletionData("**bold**", "Bold text", "**bold**", 2);
        yield return new MarkdownCompletionData("*italic*", "Italic text", "*italic*", 1);
        yield return new MarkdownCompletionData("```csharp", "C# code fence", "```csharp\n\n```", 10);
        yield return new MarkdownCompletionData("```python", "Python code fence", "```python\n\n```", 10);
        yield return new MarkdownCompletionData("```json", "JSON code fence", "```json\n\n```", 8);
        yield return new MarkdownCompletionData("$\\alpha$", "Inline LaTeX formula", "$\\alpha$", 1);
        yield return new MarkdownCompletionData("$$\\alpha$$", "Display LaTeX formula", "$$\n\\alpha\n$$", 3);
        yield return new MarkdownCompletionData("\\frac{a}{b}", "LaTeX fraction", "\\frac{a}{b}", 6);
        yield return new MarkdownCompletionData("\\sqrt{x}", "LaTeX square root", "\\sqrt{x}", 6);
        yield return new MarkdownCompletionData("| table |", "Markdown table", "| Column | Value |\n| --- | --- |\n|  |  |", 28);
    }

    private void SetViewMode(ViewMode mode)
    {
        if (_wysiwygMode)
        {
            _viewMode = ViewMode.EditorOnly;
            mode = ViewMode.EditorOnly;
        }
        else
        {
            _viewMode = mode;
        }

        var editorVisible = mode != ViewMode.PreviewOnly;
        var previewVisible = mode != ViewMode.EditorOnly;
        var splitVisible = mode == ViewMode.Both;

        EditorColumn.MinWidth = editorVisible ? 220 : 0;
        PreviewColumn.MinWidth = previewVisible ? 220 : 0;
        SplitterColumn.Width = splitVisible ? new GridLength(6) : new GridLength(0);
        EditorColumn.Width = editorVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PreviewColumn.Width = previewVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        EditorPane.Visibility = editorVisible ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = splitVisible ? Visibility.Visible : Visibility.Collapsed;
        PreviewPane.Visibility = previewVisible ? Visibility.Visible : Visibility.Collapsed;

        ViewBothMenuItem.IsChecked = mode == ViewMode.Both;
        ViewEditorMenuItem.IsChecked = mode == ViewMode.EditorOnly;
        ViewPreviewMenuItem.IsChecked = mode == ViewMode.PreviewOnly;

        if (previewVisible)
        {
            ScheduleRender();
        }

        if (editorVisible && _wysiwygMode)
        {
            _ = RenderWysiwygAsync();
        }
    }

    private async Task SetWysiwygModeAsync(bool enabled)
    {
        if (enabled && !_wysiwygReady)
        {
            StatusText.Text = "WYSIWYG mode needs Microsoft Edge WebView2 Runtime.";
            WysiwygModeMenuItem.IsChecked = false;
            SourceModeMenuItem.IsChecked = true;
            return;
        }

        if (enabled == _wysiwygMode)
        {
            SourceModeMenuItem.IsChecked = !enabled;
            WysiwygModeMenuItem.IsChecked = enabled;
            return;
        }

        if (enabled)
        {
            _viewModeBeforeWysiwyg = _viewMode == ViewMode.PreviewOnly ? ViewMode.Both : _viewMode;
        }

        if (!enabled)
        {
            await SyncWysiwygEditorAsync();
        }

        _wysiwygMode = enabled;
        SourceModeMenuItem.IsChecked = !enabled;
        WysiwygModeMenuItem.IsChecked = enabled;
        Editor.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        Wysiwyg.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (enabled)
        {
            SetViewMode(ViewMode.EditorOnly);
            await RenderWysiwygAsync();
            Wysiwyg.Focus();
            StatusText.Text = "WYSIWYG mode: the editor is the preview. Focus a block to edit its Markdown.";
        }
        else
        {
            SetViewMode(_viewModeBeforeWysiwyg);
            Editor.TextArea.Focus();
            StatusText.Text = "Source Markdown mode";
        }
    }

    private void SetWysiwygMode(bool enabled)
    {
        _ = SetWysiwygModeAsync(enabled);
    }

    private void Wysiwyg_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        WysiwygMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<WysiwygMessage>(e.WebMessageAsJson, JsonOptions);
        }
        catch
        {
            return;
        }

        if (string.Equals(message?.Type, "undo", StringComparison.OrdinalIgnoreCase))
        {
            _ = UndoWysiwygAsync(message!.Markdown);
            return;
        }

        if (string.Equals(message?.Type, "redo", StringComparison.OrdinalIgnoreCase))
        {
            _ = RedoWysiwygAsync(message!.Markdown);
            return;
        }

        if (message?.Markdown is null)
        {
            return;
        }

        UpdateTextFromWysiwyg(message.Markdown);
        if (string.Equals(message.Type, "blur", StringComparison.OrdinalIgnoreCase))
        {
            _ = RenderWysiwygAsync();
        }
    }

    private void Preview_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        WysiwygMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<WysiwygMessage>(e.WebMessageAsJson, JsonOptions);
        }
        catch
        {
            return;
        }

        if (message is null
            || !string.Equals(message.Type, "previewTask", StringComparison.OrdinalIgnoreCase)
            || message.TaskIndex is null
            || message.Checked is null)
        {
            return;
        }

        ToggleTaskCheckbox(message.TaskIndex.Value, message.Checked.Value);
    }

    private void ToggleTaskCheckbox(int taskIndex, bool isChecked)
    {
        var lines = Editor.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var seen = 0;
        var inFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            var match = Regex.Match(lines[i], @"^(\s*[-+*]\s+\[)( |x|X)(\]\s+.*)$");
            if (!match.Success)
            {
                continue;
            }

            if (seen != taskIndex)
            {
                seen++;
                continue;
            }

            lines[i] = match.Groups[1].Value + (isChecked ? "x" : " ") + match.Groups[3].Value;
            _isUpdatingFromWysiwyg = true;
            var caret = Editor.CaretOffset;
            Editor.Text = string.Join(Environment.NewLine, lines);
            Editor.CaretOffset = Math.Min(caret, Editor.Text.Length);
            _isUpdatingFromWysiwyg = false;
            _isDirty = true;
            UpdateTitle();
            ScheduleRender();
            StatusText.Text = isChecked ? "Task checked" : "Task unchecked";
            return;
        }
    }

    private void UpdateTextFromWysiwyg(string markdown)
    {
        if (Editor.Text == markdown)
        {
            return;
        }

        _isUpdatingFromWysiwyg = true;
        var caret = Math.Min(Editor.CaretOffset, markdown.Length);
        Editor.Document.Replace(0, Editor.Document.TextLength, markdown);
        Editor.CaretOffset = caret;
        _isUpdatingFromWysiwyg = false;
    }

    private async Task UndoWysiwygAsync(string? currentMarkdown = null)
    {
        if (!_wysiwygMode)
        {
            if (Editor.CanUndo)
            {
                Editor.Undo();
            }

            return;
        }

        if (currentMarkdown is not null && currentMarkdown != Editor.Text)
        {
            UpdateTextFromWysiwyg(currentMarkdown);
        }
        else
        {
            await SyncWysiwygEditorAsync();
        }

        if (!Editor.CanUndo)
        {
            return;
        }

        Editor.Undo();
        _isDirty = true;
        UpdateTitle();
        ScheduleRender();
        await RenderWysiwygAsync();
        StatusText.Text = "Undo";
    }

    private async Task RedoWysiwygAsync(string? currentMarkdown = null)
    {
        if (!_wysiwygMode)
        {
            if (Editor.CanRedo)
            {
                Editor.Redo();
            }

            return;
        }

        if (currentMarkdown is not null && currentMarkdown != Editor.Text && !Editor.CanRedo)
        {
            UpdateTextFromWysiwyg(currentMarkdown);
        }
        else if (currentMarkdown is null)
        {
            await SyncWysiwygEditorAsync();
        }

        if (!Editor.CanRedo)
        {
            return;
        }

        Editor.Redo();
        _isDirty = true;
        UpdateTitle();
        ScheduleRender();
        await RenderWysiwygAsync();
        StatusText.Text = "Redo";
    }

    private async Task SyncWysiwygEditorAsync()
    {
        if (!_wysiwygMode || !_wysiwygReady || Wysiwyg.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var result = await Wysiwyg.ExecuteScriptAsync("window.mdvGetMarkdown ? window.mdvGetMarkdown() : '';");
            var markdown = JsonSerializer.Deserialize<string>(result, JsonOptions);
            if (markdown is not null)
            {
                UpdateTextFromWysiwyg(markdown);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static bool TryGetClipboardImageReference(out string source, out string? alt)
    {
        source = string.Empty;
        alt = null;

        try
        {
            var data = Clipboard.GetDataObject();
            return data is not null && TryGetImageReference(data, out source, out alt);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetImageReference(IDataObject data, out string source, out string? alt)
    {
        source = string.Empty;
        alt = null;

        try
        {
            if (data.GetDataPresent(DataFormats.Html)
                && data.GetData(DataFormats.Html) is string html
                && TryExtractHtmlImageReference(html, out source, out alt))
            {
                return true;
            }

            if (data.GetDataPresent(DataFormats.Text)
                && data.GetData(DataFormats.Text) is string text
                && IsImageReferenceText(text))
            {
                source = text.Trim();
                alt = null;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryExtractHtmlImageReference(string html, out string source, out string? alt)
    {
        source = string.Empty;
        alt = null;

        var image = Regex.Match(html, "<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (image.Success)
        {
            var src = GetHtmlAttribute(image.Value, "src");
            if (!string.IsNullOrWhiteSpace(src))
            {
                source = WebUtility.HtmlDecode(src);
                alt = WebUtility.HtmlDecode(GetHtmlAttribute(image.Value, "alt") ?? string.Empty);
                return true;
            }
        }

        var link = Regex.Match(html, "<a\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (link.Success)
        {
            var href = GetHtmlAttribute(link.Value, "href");
            if (!string.IsNullOrWhiteSpace(href) && IsImageReferenceText(href))
            {
                source = WebUtility.HtmlDecode(href);
                return true;
            }
        }

        return false;
    }

    private static string? GetHtmlAttribute(string tag, string name)
    {
        var match = Regex.Match(
            tag,
            "\\b" + Regex.Escape(name) + "\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Success
            ? match.Groups[1].Value
            : match.Groups[2].Success
                ? match.Groups[2].Value
                : match.Groups[3].Value;
    }

    private static bool IsImageReferenceText(string text)
    {
        var value = text.Trim();
        if (value.Length == 0 || value.Contains('\n') || value.Contains('\r'))
        {
            return false;
        }

        try
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                    || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                    || uri.IsFile)
                {
                    return IsImageExtension(uri.LocalPath);
                }
            }

            if (Path.IsPathFullyQualified(value))
            {
                return IsImageFile(value);
            }
        }
        catch
        {
        }

        return false;
    }

    private async Task InsertImageAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The selected image does not exist.", path);
            }

            var markdown = CreateImageMarkdown(path);
            await InsertMarkdownImageAsync(markdown);
            StatusText.Text = $"Inserted image {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Image insert failed.";
            MessageBox.Show(this, ex.Message, "Insert image failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InsertImageReferenceAsync(string source, string? alt)
    {
        try
        {
            var markdown = CreateImageMarkdownFromSource(source, alt);
            await InsertMarkdownImageAsync(markdown);
            StatusText.Text = "Inserted image reference";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Image insert failed.";
            MessageBox.Show(this, ex.Message, "Insert image failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InsertMarkdownImageAsync(string markdown)
    {
        if (_wysiwygMode && _wysiwygReady && Wysiwyg.CoreWebView2 is not null)
        {
            var payload = JsonSerializer.Serialize(markdown, JsonOptions);
            await Wysiwyg.CoreWebView2.ExecuteScriptAsync($"window.mdvInsertMarkdown ? window.mdvInsertMarkdown({payload}) : false;");
            await SyncWysiwygEditorAsync();
            await RenderWysiwygAsync();
        }
        else
        {
            var offset = Math.Clamp(Editor.CaretOffset, 0, Editor.Document.TextLength);
            Editor.Document.Insert(offset, markdown);
            Editor.CaretOffset = Math.Min(offset + markdown.Length, Editor.Document.TextLength);
            Editor.TextArea.Focus();
        }

        _isDirty = true;
        UpdateTitle();
        ScheduleRender();
    }

    private string CreateImageMarkdown(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var alt = CleanImageAltText(Path.GetFileNameWithoutExtension(fullPath));
        var target = PrepareImageTarget(fullPath);
        return $"![{alt}]({target})";
    }

    private string CreateImageMarkdownFromSource(string source, string? alt)
    {
        var cleanSource = WebUtility.HtmlDecode(source).Trim();
        if (TryGetLocalImagePath(cleanSource, out var localPath))
        {
            var localAlt = string.IsNullOrWhiteSpace(alt)
                ? Path.GetFileNameWithoutExtension(localPath)
                : alt;
            return $"![{CleanImageAltText(localAlt)}]({PrepareImageTarget(localPath)})";
        }

        var imageAlt = string.IsNullOrWhiteSpace(alt)
            ? GuessImageAltText(cleanSource)
            : alt;
        return $"![{CleanImageAltText(imageAlt)}]({EscapeMarkdownImageTarget(cleanSource)})";
    }

    private string PrepareImageTarget(string fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(fullPath);
            var baseFolder = GetDocumentBaseFolder();
            if (IsPathInsideFolder(fullPath, baseFolder))
            {
                var relative = Path.GetRelativePath(baseFolder, fullPath).Replace('\\', '/');
                return EscapeMarkdownImageTarget(relative);
            }

            return EscapeMarkdownImageTarget(fullPath);
        }
        catch
        {
            return EscapeMarkdownImageTarget(fullPath);
        }
    }

    private static string EscapeMarkdownImageTarget(string target)
    {
        return target
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal)
            .Replace("#", "%23", StringComparison.Ordinal)
            .Replace("?", "%3F", StringComparison.Ordinal);
    }

    private static string CleanImageAltText(string? text)
    {
        var clean = string.IsNullOrWhiteSpace(text) ? "image" : text.Trim();
        return clean
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("[", "(", StringComparison.Ordinal)
            .Replace("]", ")", StringComparison.Ordinal);
    }

    private static string GuessImageAltText(string source)
    {
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
            {
                var name = Path.GetFileNameWithoutExtension(uri.LocalPath);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return Uri.UnescapeDataString(name);
                }
            }
        }
        catch
        {
        }

        var fileName = Path.GetFileNameWithoutExtension(source);
        return string.IsNullOrWhiteSpace(fileName) ? "image" : fileName;
    }

    private static bool TryGetLocalImagePath(string source, out string path)
    {
        path = string.Empty;
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                path = uri.LocalPath;
                return File.Exists(path) && IsImageFile(path);
            }

            if (Path.IsPathFullyQualified(source))
            {
                path = Path.GetFullPath(source);
                return File.Exists(path) && IsImageFile(path);
            }
        }
        catch
        {
        }

        return false;
    }

    private void UpdateTitle()
    {
        var name = _currentFilePath is null ? "Untitled.md" : Path.GetFileName(_currentFilePath);
        Title = $"{(_isDirty ? "*" : string.Empty)}{name} - MarkDNext";
    }

    private void UpdatePosition()
    {
        try
        {
            var caret = Editor.TextArea.Caret;
            PositionText.Text = string.Format(CultureInfo.CurrentCulture, "Ln {0}, Col {1}", caret.Line, caret.Column);
        }
        catch
        {
            PositionText.Text = "Ln 1, Col 1";
        }
    }

    private enum ViewMode
    {
        Both,
        EditorOnly,
        PreviewOnly
    }

    private sealed record WysiwygBlock(int Index, string Source, string Html, string Kind);

    private sealed record MarkdownSourceBlock(int StartLine, int EndLine, string Source);

    private sealed record WysiwygMessage(string? Type, string? Markdown, int? TaskIndex, bool? Checked);

    private sealed record MathSegment(string Placeholder, string Source, bool Display);

    private sealed record ImageSegment(string Placeholder, string Alt, string Target, string? Title);

    private sealed record ColorProfile(
        string Page,
        string Text,
        string Muted,
        string Heading,
        string Line,
        string Code,
        string Accent,
        string Surface,
        string Window,
        string Chrome,
        string EditorBackground,
        string EditorText,
        string QuoteBackground)
    {
        public static ColorProfile Default { get; } = new(
            "#ffffff",
            "#1f2933",
            "#5d6978",
            "#111827",
            "#d9e0e8",
            "#f4f6f8",
            "#1769aa",
            "#f7ffffff",
            "#f6f7f9",
            "#eaf3f6fa",
            "#00ffffff",
            "#182433",
            "#f8fafc");

        public ColorProfile Normalized()
        {
            return this with
            {
                Page = NormalizeColor(Page, Default.Page),
                Text = NormalizeColor(Text, Default.Text),
                Muted = NormalizeColor(Muted, Default.Muted),
                Heading = NormalizeColor(Heading, Default.Heading),
                Line = NormalizeColor(Line, Default.Line),
                Code = NormalizeColor(Code, Default.Code),
                Accent = NormalizeColor(Accent, Default.Accent),
                Surface = NormalizeColor(Surface, Default.Surface),
                Window = NormalizeColor(Window, Default.Window),
                Chrome = NormalizeColor(Chrome, Default.Chrome),
                EditorBackground = NormalizeColor(EditorBackground, Default.EditorBackground),
                EditorText = NormalizeColor(EditorText, Default.EditorText),
                QuoteBackground = NormalizeColor(QuoteBackground, Default.QuoteBackground)
            };
        }

        private static string NormalizeColor(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            try
            {
                _ = ColorConverter.ConvertFromString(value);
                return value.Trim();
            }
            catch
            {
                return fallback;
            }
        }
    }
}
