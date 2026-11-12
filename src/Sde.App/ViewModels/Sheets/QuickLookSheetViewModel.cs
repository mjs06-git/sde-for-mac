using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.Core.Photos;
using Sde.Reports;

namespace Sde.App.ViewModels.Sheets;

/// <summary>
/// Quick Look: one of the assessment's photos large on a dark stage, with its description and "3 of 12". Left and Right
/// (or the chevrons) step through the photos, Esc or Space closes, and Mark Up opens the markup sheet for the photo. A
/// marked-up photo shows its marks, as its thumbnail does. Each photo is decoded off the UI thread at most
/// <see cref="MaxEdge"/> pixels on its long side, so a large photo doesn't hold the app up; the picture is let go when
/// the next one replaces it and when the sheet closes.
/// </summary>
public sealed partial class QuickLookSheetViewModel : SheetViewModel
{
	/// <summary>The long edge of the decoded picture: twice the stage's width, so it stays sharp on a Retina screen.</summary>
	public const int MaxEdge = 1600;

	private readonly IReadOnlyList<PhotoViewModel> _photos;
	private readonly Func<PhotoViewModel, Task> _markUp;
	private int _generation;

	/// <param name="markUp">Opens the markup sheet for a photo (the editor's own Mark up).</param>
	public QuickLookSheetViewModel(IReadOnlyList<PhotoViewModel> photos, int index, Func<PhotoViewModel, Task> markUp) : base("Quick Look")
	{
		_photos = photos;
		_markUp = markUp;
		Index = Math.Clamp(index, 0, Math.Max(0, photos.Count - 1));
		Closing += _ => Release();
		Loaded = LoadAsync();
	}

	public override SheetSize Size => SheetSize.Large;

	/// <summary>The photo showing, 0-based.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Current), nameof(PositionText), nameof(Caption), nameof(HasCaption), nameof(CaptionText), nameof(FileName), nameof(AccessibleName), nameof(MarkUpText))]
	[NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand))]
	public partial int Index { get; private set; }

	public int Count => _photos.Count;

	public PhotoViewModel Current => _photos.Count == 0 ? null : _photos[Index];

	/// <summary>"3 of 12".</summary>
	public string PositionText => $"{Index + 1} of {Count}";

	/// <summary>The photo's description.</summary>
	public string Caption => Current?.Description;

	public bool HasCaption => !string.IsNullOrWhiteSpace(Caption);

	/// <summary>The description under the photo, or "No description".</summary>
	public string CaptionText => HasCaption ? Caption : "No description";

	public string FileName => Current?.FileName ?? "";

	/// <summary>What VoiceOver reads for the picture.</summary>
	public string AccessibleName => $"Photo {PositionText}" + (HasCaption ? ": " + Caption : "");

	public string MarkUpText => Current?.HasMarkup == true ? "Change Marks…" : "Mark Up…";

	/// <summary>The decoded picture (with its marks drawn on), or null while it loads or when it can't be read.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasImage), nameof(IsUnreadable))]
	public partial Bitmap Image { get; private set; }

	public bool HasImage => Image != null;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsUnreadable))]
	public partial bool IsLoading { get; private set; }

	/// <summary>The photo couldn't be decoded (not a picture this Mac reads); its file name is shown instead.</summary>
	public bool IsUnreadable => !IsLoading && Image == null;

	/// <summary>True when the marked-up picture was drawn (for the tests: the marks are on what is shown).</summary>
	public bool ShowsMarkup { get; private set; }

	/// <summary>Completes when the photo showing has been decoded (or found unreadable).</summary>
	public Task Loaded { get; private set; }

	private bool CanGoPrevious => Index > 0;

	private bool CanGoNext => Index < Count - 1;

	[RelayCommand(CanExecute = nameof(CanGoPrevious))]
	private void Previous() => Go(Index - 1);

	[RelayCommand(CanExecute = nameof(CanGoNext))]
	private void Next() => Go(Index + 1);

	private void Go(int index)
	{
		if (index < 0 || index >= Count || index == Index || IsClosed)
		{
			return;
		}
		Index = index;
		Loaded = LoadAsync();
	}

	/// <summary>Mark Up…: the markup sheet over this one; the picture is drawn again with the new marks when it closes.</summary>
	[RelayCommand]
	private async Task MarkUp()
	{
		if (Current == null || _markUp == null)
		{
			return;
		}
		await _markUp(Current);
		if (!IsClosed)
		{
			OnPropertyChanged(nameof(MarkUpText));
			Loaded = LoadAsync();
		}
	}

	private async Task LoadAsync()
	{
		PhotoViewModel photo = Current;
		int generation = ++_generation;
		if (photo == null)
		{
			return;
		}
		IsLoading = true;
		string path = photo.Path;
		PhotoMarkup markup = photo.HasMarkup ? photo.Markup : null;
		Bitmap decoded = await Task.Run(() => Decode(path, markup));
		if (generation != _generation || IsClosed)
		{
			// Another photo was asked for meanwhile, or the sheet closed: this one isn't wanted.
			decoded?.Dispose();
			return;
		}
		Bitmap old = Image;
		Image = decoded;
		ShowsMarkup = decoded != null && markup != null;
		IsLoading = false;
		old?.Dispose();
	}

	/// <summary>Reads the photo upright and at most <see cref="MaxEdge"/> pixels long, with its marks drawn on; null when it can't be read.</summary>
	private static Bitmap Decode(string path, PhotoMarkup markup)
	{
		try
		{
			if (!File.Exists(path) || PhotoMarkupRenderer.Render(path, markup, MaxEdge) is not (byte[] jpeg, _, _))
			{
				return null;
			}
			return new Bitmap(new MemoryStream(jpeg));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Quick Look couldn't read a photo", ex);
			return null;
		}
	}

	private void Release()
	{
		_generation++;
		Bitmap old = Image;
		Image = null;
		old?.Dispose();
	}
}
