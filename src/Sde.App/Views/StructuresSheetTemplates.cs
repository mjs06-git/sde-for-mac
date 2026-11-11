using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Sde.App.ViewModels.Sheets;

namespace Sde.App.Views;

/// <summary>
/// The Structures page's sheets (Submission Folder, Manage Tags, New Tag) and the views that show them: the main window
/// lists this once among its data templates, as it does the map's.
/// </summary>
public sealed class StructuresSheetTemplates : IDataTemplate
{
	private static readonly Dictionary<Type, Func<Control>> Views = new Dictionary<Type, Func<Control>>
	{
		[typeof(SubmissionSheetViewModel)] = () => new SubmissionSheetView(),
		[typeof(ManageTagsSheetViewModel)] = () => new ManageTagsSheetView(),
		[typeof(TagSheetViewModel)] = () => new TagSheetView(),
	};

	public bool Match(object data) => data != null && Views.ContainsKey(data.GetType());

	public Control Build(object param) => param != null && Views.TryGetValue(param.GetType(), out Func<Control> make) ? make() : null;
}
