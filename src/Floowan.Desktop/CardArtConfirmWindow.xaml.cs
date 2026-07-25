using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Floowan.Core.Imaging;
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

        TitleText.Text = string.IsNullOrWhiteSpace(cardDisplayName)
            ? "Confirm card art replacement"
            : $"Replace art for \"{cardDisplayName}\"?";
        BeforeMetaText.Text = beforeMeta;
        AfterMetaText.Text = afterMeta;

        BeforeImage.Source = flattenBeforeFoilMask
            ? LoadFoilFlattenedBitmap(beforeImagePath)
            : LoadBitmap(beforeImagePath);
        AfterImage.Source = LoadBitmap(afterImagePath);

        if (flattenBeforeFoilMask)
        {
            SubtitleText.Text =
                "Live art is over-frame sized. Before uses a foil-flattened preview for visibility. " +
                "Confirm still uses the existing Card Art replace path (bundle backup; no illustration PNG for OF faces).";
            BeforeImage.ToolTip =
                "Current live over-frame texture (foil mask flattened for preview only).";
        }
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
