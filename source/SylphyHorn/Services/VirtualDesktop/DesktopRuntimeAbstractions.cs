using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using SylphyHorn.Serialization;
using WindowsDesktop;

namespace SylphyHorn.Services.DesktopTransitions
{
	internal interface IDesktopProviderClient : IDisposable
	{
		event EventHandler<VirtualDesktopStableBatch> StableBatchPublished;
		event EventHandler<VirtualDesktopCurrentTransition> CurrentTransitioned;
		event EventHandler<VirtualDesktopProviderFault> Faulted;
		Task<VirtualDesktopReconciliationResult> RequestReconciliationAsync(VirtualDesktopStableReason reason, CancellationToken cancellationToken);
	}

	internal interface IDesktopSettingsTransactions
	{
		DesktopStartupSeed CaptureStartupSeed();
		DesktopWallpaperOnCreation[] ReadWallpapersOnCreation();
		bool PerDesktopWallpaperEnabled { get; }
		SylphyHorn.AppPlacement.PlacementCreatedGroup[] ReadCreatedDesktopGroups();
		void WriteCreatedDesktopGroups(SylphyHorn.AppPlacement.PlacementCreatedGroup[] groups);
		void ApplyProjection(DesktopSettingsProjection projection);
		long SettingsRevision { get; }
		Task<SettingsSaveResult> RequestSaveAsync(long stateRevision);
		Task<StagedSettingsImport> PrepareImportAsync(string path);
		Task<StagedSettingsImport> PrepareResetAsync();
		DesktopSettingsImportClaim ClaimImport(StagedSettingsImport stage);
		Task<SettingsImportCommitResult> CommitImportAsync(DesktopSettingsImportClaim claim, IDictionary<string, object> dictionary);
		SettingsImportCommitResult DiscardImport(DesktopSettingsImportClaim claim);
		void PublishImportCommitted();
	}

	internal interface IDesktopOwnerContext
	{
		bool CheckAccess();
		bool Post(Action action);
	}

	internal interface IDesktopOperations
	{
		Guid Create();
		void SetName(Guid desktopId, string value);
		void SetWallpaperPath(Guid desktopId, string value);
		void ApplyWallpaper(Guid desktopId, string value, WallpaperPosition position);
		void MoveLeft(Guid desktopId);
		void MoveRight(Guid desktopId);
		void MoveFirst(Guid desktopId);
		void MoveLast(Guid desktopId);
		void Switch(Guid desktopId);
		void Remove(Guid desktopId, Guid? fallbackId = null);
		bool TryRemoveEmpty(Guid desktopId, Guid fallbackId, Func<bool> stillCurrent);
	}

}
