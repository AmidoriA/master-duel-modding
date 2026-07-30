using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Floowan.Core.Imaging;
using Floowan.Desktop.Localization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;

namespace Floowan.Desktop;

public partial class CardArtConfirmWindow : Window
{
    public CardArtConfirmWindow(
        string cardDisplayName,
        string beforeImagePath,
        string afterImagePath,
        string beforeMeta,
        string afterMeta,
        bool flattenBeforeFoilMask = false)
    {
        InitializeComponent();
        ApplyLocalizedChrome();

        TitleText.Text = string.IsNullOrWhiteSpace(cardDisplayName)
            ? Loc.T("confirm.title")
            : Loc.T("confirm.title_named", cardDisplayName);
        BeforeMetaText.Text = beforeMeta;
        AfterMetaText.Text = afterMeta;

        BeforeImage.Source = flattenBeforeFoilMask
            ? LoadFoilFlattenedBitmap(beforeImagePath)
            : LoadBitmap(beforeImagePath);
        AfterImage.Source = LoadBitmap(afterImagePath);

        if (flattenBeforeFoilMask)
        {
            SubtitleText.Text = Loc.T("confirm.subtitle_foil");
            BeforeImage.ToolTip = Loc.T("confirm.before_foil_tooltip");
        }
    }

    private void ApplyLocalizedChrome()
    {
        Title = Loc.T("confirm.title");
        SubtitleText.Text = Loc.T("confirm.subtitle");
        CancelButton.Content = Loc.T("common.cancel");
        CancelButton.ToolTip = Loc.T("confirm.cancel_tooltip");
        ConfirmButton.Content = Loc.T("common.confirm");
        ConfirmButton.ToolTip = Loc.T("confirm.confirm_tooltip");
        BeforeHeaderText.Text = Loc.T("confirm.before");
        AfterHeaderText.Text = Loc.T("confirm.after");
        BeforeImage.ToolTip = Loc.T("confirm.before_tooltip");
        AfterImage.ToolTip = Loc.T("confirm.after_tooltip");
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// OF textures use a foil mask that WPF treats as nearly invisible. Flatten for preview only.
    /// </summary>
    private static BitmapImage LoadFoilFlattenedBitmap(string path)
    {
        using var image = Image.Load<Rgba32>(path);
        using var flat = OverFrameAutoArtComposer.FlattenFoilMaskForPreview(image);
        using var ms = new MemoryStream();
        flat.Save(ms, new PngEncoder());
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
