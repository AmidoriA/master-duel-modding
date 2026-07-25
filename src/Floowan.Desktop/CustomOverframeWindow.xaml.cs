using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Floowan.Core.Assets;
using Floowan.Core.Data;
using Floowan.Core.Imaging;
using Floowan.Core.Models;
using Floowan.Core.Services;
using Microsoft.Win32;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace Floowan.Desktop;

public partial class CustomOverframeWindow : Window
{
    private readonly AutoOverFrameArtService _autoArt;
    private readonly OverFrameModService _overFrameService;
    private readonly CardRecord _card;
    private readonly string _gamePath;
    private readonly CardDatabase? _database;

    private Image<Rgba32>? _subjectSource;
    private Image<L8>? _subjectMask;
    private string? _composedTempPath;
    private int _offsetX;
    private int _offsetY;
    private bool _busy;
    private bool _dragging;
    private System.Windows.Point _dragStart;
    private int _dragStartOffsetX;
    private int _dragStartOffsetY;

    public string? ResultMessage { get; private set; }

    public CustomOverframeWindow(
        AutoOverFrameArtService autoArt,
        OverFrameModService overFrameService,
        CardRecord card,
        string gamePath,
        CardDatabase? database,
        CardFrameStyle initialFrameStyle)
    {
        InitializeComponent();
        _autoArt = autoArt;
        _overFrameService = overFrameService;
        _card = card;
        _gamePath = gamePath;
        _database = database;
        Title = $"Custom overframe art — {card.DisplayName}";
        SelectFrameStyle(initialFrameStyle);
        Closed += (_, _) => Cleanup();
    }

    private void SelectFrameStyle(CardFrameStyle style)
    {
        for (var i = 0; i < FrameStyleBox.Items.Count; i++)
        {
            if (FrameStyleBox.Items[i] is ComboBoxItem item
                && item.Tag is string tag
                && Enum.TryParse<CardFrameStyle>(tag, out var parsed)
                && parsed == style)
            {
                FrameStyleBox.SelectedIndex = i;
                return;
            }
        }

        FrameStyleBox.SelectedIndex = 0; // Effect
    }

    private CardFrameStyle GetSelectedFrameStyle()
    {
        if (FrameStyleBox.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && Enum.TryParse<CardFrameStyle>(tag, out var selected))
        {
            return selected;
        }

        return CardFrameStyle.Effect;
    }

    private async void PickImage_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var dlg = new OpenFileDialog
        {
            Title = "Select subject image for custom overframe",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var validation = ImagePreparation.Validate(
            dlg.FileName, OverFrameConstants.Width, OverFrameConstants.Height);
        if (!validation.IsValid)
        {
            MessageBox.Show(
                this,
                validation.Error ?? "Invalid image.",
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var sizeNote = CardArtTextureSizes.Describe(validation.Width, validation.Height);
        if (validation.Width != OverFrameConstants.Width
            || validation.Height != OverFrameConstants.Height)
        {
            var proceed = MessageBox.Show(
                this,
                $"Image is {sizeNote}; preferred source is 704×1024.\n\n" +
                (validation.Warning ?? "It will be prepared through the Auto-create rembg pipeline.") +
                "\n\nContinue?",
                "Custom overframe art",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (proceed != MessageBoxResult.Yes)
                return;
        }

        await PrepareFromImageAsync(dlg.FileName, sizeNote);
    }

    private async Task PrepareFromImageAsync(string imagePath, string sizeNote)
    {
        SetBusy(true);
        StatusText.Text = $"Preparing {Path.GetFileName(imagePath)} ({sizeNote})…";
        try
        {
            DisposeSubject();
            _offsetX = 0;
            _offsetY = 0;
            ResetDragTransform();

            var progress = new Progress<string>(msg => StatusText.Text = msg);
            var prepared = await _autoArt.PrepareSubjectAsync(imagePath, progress);
            _subjectSource = prepared.Source;
            _subjectMask = prepared.Mask;

            await RecomposePreviewAsync();
            StatusText.Text =
                $"Subject ready ({sizeNote}). Drag to reposition, then Apply.";
            PreviewHintText.Visibility = Visibility.Collapsed;
            ApplyButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            DisposeSubject();
            PreviewImage.Source = null;
            PreviewHintText.Visibility = Visibility.Visible;
            ApplyButton.IsEnabled = false;
            StatusText.Text = "Failed: " + ex.Message;
            MessageBox.Show(
                this,
                "Could not prepare custom overframe art:\n\n" + ex.Message,
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void FrameStyleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_subjectSource is null || _subjectMask is null || _busy)
            return;

        SetBusy(true);
        try
        {
            await RecomposePreviewAsync();
            StatusText.Text =
                $"Preview updated ({GetSelectedFrameStyle()}). Drag to reposition, then Apply.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Compose failed: " + ex.Message;
            MessageBox.Show(
                this,
                "Could not recompose overframe:\n\n" + ex.Message,
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RecomposePreviewAsync()
    {
        if (_subjectSource is null || _subjectMask is null)
            return;

        var frameStyle = GetSelectedFrameStyle();
        var offsetX = _offsetX;
        var offsetY = _offsetY;
        var source = _subjectSource;
        var mask = _subjectMask;

        CleanupComposedTemp();
        _composedTempPath = Path.Combine(
            Path.GetTempPath(),
            $"floowan-custom-of-{Guid.NewGuid():N}.png");

        var outputPath = _composedTempPath;
        await Task.Run(() =>
            AutoOverFrameArtService.ComposePreparedSubject(
                source,
                mask,
                outputPath,
                frameStyle,
                offsetX,
                offsetY));

        PreviewImage.Source = LoadOfComposePreview(outputPath);
        ResetDragTransform();
    }

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_subjectSource is null || _busy || PreviewImage.Source is null)
            return;

        _dragging = true;
        _dragStart = e.GetPosition(PreviewHost);
        _dragStartOffsetX = _offsetX;
        _dragStartOffsetY = _offsetY;
        PreviewHost.CaptureMouse();
        e.Handled = true;
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        var pos = e.GetPosition(PreviewHost);
        var scale = GetPreviewCanvasScale();
        if (scale <= 0)
            return;

        var dx = (pos.X - _dragStart.X) / scale;
        var dy = (pos.Y - _dragStart.Y) / scale;
        PreviewDragTransform.X = pos.X - _dragStart.X;
        PreviewDragTransform.Y = pos.Y - _dragStart.Y;
        StatusText.Text =
            $"Offset {_dragStartOffsetX + (int)Math.Round(dx)}, {_dragStartOffsetY + (int)Math.Round(dy)} (release to recompose)";
    }

    private async void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;

        await FinishDragAsync(e.GetPosition(PreviewHost));
    }

    private async void Preview_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton == MouseButtonState.Pressed)
            return;

        await FinishDragAsync(e.GetPosition(PreviewHost));
    }

    private async void Preview_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        await FinishDragAsync(e.GetPosition(PreviewHost));
    }

    private async Task FinishDragAsync(System.Windows.Point pos)
    {
        if (!_dragging)
            return;

        _dragging = false;
        PreviewHost.ReleaseMouseCapture();

        var scale = GetPreviewCanvasScale();
        if (scale > 0)
        {
            var dx = (pos.X - _dragStart.X) / scale;
            var dy = (pos.Y - _dragStart.Y) / scale;
            _offsetX = _dragStartOffsetX + (int)Math.Round(dx);
            _offsetY = _dragStartOffsetY + (int)Math.Round(dy);
        }

        if (_busy || _subjectSource is null || _subjectMask is null)
        {
            ResetDragTransform();
            return;
        }

        SetBusy(true);
        StatusText.Text = $"Recomposing at offset {_offsetX}, {_offsetY}…";
        try
        {
            await RecomposePreviewAsync();
            StatusText.Text =
                $"Preview at offset {_offsetX}, {_offsetY}. Drag again or Apply.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Compose failed: " + ex.Message;
            ResetDragTransform();
            MessageBox.Show(
                this,
                "Could not recompose overframe:\n\n" + ex.Message,
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Maps preview control pixels to 704×1024 canvas pixels (Uniform stretch).
    /// </summary>
    private double GetPreviewCanvasScale()
    {
        if (PreviewImage.Source is not BitmapSource bmp)
            return 0;

        var availableW = PreviewHost.ActualWidth;
        var availableH = PreviewHost.ActualHeight;
        if (availableW <= 0 || availableH <= 0)
            return 0;

        return Math.Min(availableW / bmp.PixelWidth, availableH / bmp.PixelHeight);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || string.IsNullOrWhiteSpace(_composedTempPath) || !File.Exists(_composedTempPath))
        {
            MessageBox.Show(
                this,
                "Compose a preview first (select an image).",
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        StatusText.Text = "Applying over-frame (texture + gate)…";
        try
        {
            var imagePath = _composedTempPath;
            var result = await Task.Run(() =>
                _overFrameService.ApplyOverFrame(
                    _gamePath,
                    _card,
                    imagePath,
                    createBackup: true,
                    _database));

            ResultMessage = result.Message;
            if (!result.Success)
            {
                StatusText.Text = result.Message;
                MessageBox.Show(
                    this,
                    result.Message,
                    "Custom overframe art",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Apply failed: " + ex.Message;
            MessageBox.Show(
                this,
                ex.Message,
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        PickImageButton.IsEnabled = !busy;
        FrameStyleBox.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy && _subjectSource is not null && _composedTempPath is not null;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    private void ResetDragTransform()
    {
        PreviewDragTransform.X = 0;
        PreviewDragTransform.Y = 0;
    }

    private void DisposeSubject()
    {
        _subjectSource?.Dispose();
        _subjectMask?.Dispose();
        _subjectSource = null;
        _subjectMask = null;
    }

    private void CleanupComposedTemp()
    {
        if (_composedTempPath is not null && File.Exists(_composedTempPath))
        {
            try { File.Delete(_composedTempPath); } catch { /* ignore */ }
        }

        _composedTempPath = null;
    }

    private void Cleanup()
    {
        DisposeSubject();
        CleanupComposedTemp();
    }

    private static BitmapImage LoadOfComposePreview(string path)
    {
        using var image = ImageSharpImage.Load<Rgba32>(path);
        using var flat = OverFrameAutoArtComposer.FlattenFoilMaskForPreview(image);
        using var ms = new MemoryStream();
        flat.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        ms.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
