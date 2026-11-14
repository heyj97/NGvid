using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using NGL.Core;
using NGL.Media;

namespace NGL.App;

/// <summary>리소스 보관함에 보이는 원본 하나.</summary>
public partial class AssetItem(MediaAsset asset, Action changed) : ObservableObject
{
    public MediaAsset Asset { get; } = asset;

    [ObservableProperty] private ImageSource? _thumbnail;
    [ObservableProperty] private bool _isMissing;

    /// <summary>이름을 바꾸면 이 원본을 쓰는 모든 클립의 표시 이름이 함께 바뀐다(클립은 원본을 가리키기만 한다).</summary>
    public string Name
    {
        get => Asset.Name;
        set
        {
            var name = value.Trim();
            if (name.Length == 0 || name == Asset.Name) return;
            Asset.Name = name;
            OnPropertyChanged();
            changed();
        }
    }

    /// <summary>분:초. 이미지는 비어 있다.</summary>
    public string DurationText => Asset.Kind == MediaKind.Image
        ? ""
        : $"{(int)Asset.Duration.TotalMinutes}:{Asset.Duration.Seconds:00}";

    public void SetThumbnail(Thumbnail? thumbnail)
    {
        if (thumbnail is null) return;
        var bitmap = BitmapSource.Create(thumbnail.Width, thumbnail.Height, 96, 96, PixelFormats.Bgr32, null,
            thumbnail.Pixels, thumbnail.Width * 4);
        bitmap.Freeze();
        Thumbnail = bitmap;
    }
}
