using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.Field;

/// <summary>
/// How the phone form saves. The Mac app implements it with its write coordinator, so a change from a phone reaches an
/// assessment open in the editor; <see cref="DirectFieldWriter"/> saves straight to the store (tests, command line).
/// </summary>
public interface IFieldWriter
{
	/// <summary>Loads an assessment, changes it, and saves it (recalculated, with history and lock checks).</summary>
	Task<AssessmentFile> ApplyAsync(Guid assessmentId, Action<AssessmentFile> change, SaveContext context);

	/// <summary>Saves a new assessment (and its structure).</summary>
	Task<AssessmentFile> AddAsync(AssessmentFile file, SaveContext context);

	/// <summary>
	/// Runs <paramref name="setStatus"/>, a status change of one assessment through the store (which checks locks and
	/// check-outs and records it in edit history). By default an open editor on the Mac is saved first and caught up after,
	/// through <see cref="ApplyAsync"/> with no change (which writes nothing when nothing changed). A writer that can run
	/// the change inside its own write coordination does so instead.
	/// </summary>
	async Task SetStatusAsync(Guid assessmentId, Action setStatus, SaveContext context)
	{
		await ApplyAsync(assessmentId, _ => { }, context);
		setStatus();
		await ApplyAsync(assessmentId, _ => { }, context);
	}
}

/// <summary>Turns a photo into one SDE can use (HEIC to JPEG on a Mac).</summary>
public interface IPhotoConverter
{
	/// <summary>
	/// The file to store: <paramref name="path"/> itself, or a converted copy in <paramref name="tempFolder"/>. Throws
	/// <see cref="IOException"/> when a conversion fails.
	/// </summary>
	string ToCompatible(string path, string tempFolder);
}

/// <summary>Keeps every photo as it is (used where no converter is available).</summary>
public sealed class KeepPhotoConverter : IPhotoConverter
{
	public string ToCompatible(string path, string tempFolder) => path;
}

/// <summary>Saves straight to the store, one write at a time.</summary>
public sealed class DirectFieldWriter(Func<SdeStore> store) : IFieldWriter
{
	private readonly object _gate = new object();

	public Task<AssessmentFile> ApplyAsync(Guid assessmentId, Action<AssessmentFile> change, SaveContext context)
	{
		lock (_gate)
		{
			return Task.FromResult(store().Apply(assessmentId, change, context));
		}
	}

	public Task<AssessmentFile> AddAsync(AssessmentFile file, SaveContext context)
	{
		lock (_gate)
		{
			store().SaveAssessment(file, context);
			return Task.FromResult(file);
		}
	}

	public Task SetStatusAsync(Guid assessmentId, Action setStatus, SaveContext context)
	{
		lock (_gate)
		{
			setStatus();
			return Task.CompletedTask;
		}
	}
}
