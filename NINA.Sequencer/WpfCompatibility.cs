#if !WINDOWS
// Compatibility stubs for NINA.WPF.Base types when building for non-Windows targets.
// These stubs allow the Sequencer to compile on net9.0 without WPF.

namespace NINA.WPF.Base.Interfaces.ViewModel {
    public interface IImageHistoryVM : NINA.Equipment.Interfaces.ViewModel.IDockableVM {
        NINA.Core.Utility.AsyncObservableCollection<NINA.WPF.Base.Model.ImageHistoryPoint> AutoFocusPoints { get; set; }
        System.Collections.Generic.List<NINA.WPF.Base.Model.ImageHistoryPoint> ImageHistory { get; }
        NINA.Core.Utility.AsyncObservableCollection<NINA.WPF.Base.Model.ImageHistoryPoint> ObservableImageHistory { get; set; }
        System.Windows.Input.ICommand PlotClearCommand { get; }
        int GetNextImageId();
        void Add(int id, NINA.Image.Interfaces.IImageStatistics statistics, string imageType);
        void Add(int id, string imageType);
        void PopulateStatistics(int id, NINA.Image.Interfaces.IImageStatistics statistics);
        void AppendImageProperties(NINA.WPF.Base.Interfaces.Mediator.ImageSavedEventArgs imageSavedEventArgs);
        void AppendAutoFocusPoint(NINA.WPF.Base.Utility.AutoFocus.AutoFocusReport report);
        void PlotClear();
    }

    public interface IAutoFocusVM { }
    public interface IApplicationStatusVM { }
}

namespace NINA.WPF.Base.Interfaces {
    public interface IAutoFocusVMFactory : NINA.Core.Interfaces.IPluggableBehavior<IAutoFocusVMFactory> {
        NINA.WPF.Base.Interfaces.ViewModel.IAutoFocusVM Create();
    }
    public interface IMeridianFlipVMFactory { }
}

namespace NINA.WPF.Base.Interfaces.Mediator {
    public interface IImageSaveMediator : NINA.Core.Interfaces.IMediator<IImageSaveController> {
        System.Threading.Tasks.Task Enqueue(NINA.Image.Interfaces.IImageData imageData, System.Threading.Tasks.Task<NINA.Image.Interfaces.IRenderedImage> prepareTask, System.IProgress<NINA.Core.Model.ApplicationStatus> progress, System.Threading.CancellationToken token);
        event System.Func<object, BeforeImageSavedEventArgs, System.Threading.Tasks.Task> BeforeImageSaved;
        event System.Func<object, BeforeFinalizeImageSavedEventArgs, System.Threading.Tasks.Task> BeforeFinalizeImageSaved;
        event System.EventHandler<ImageSavedEventArgs> ImageSaved;
        void Shutdown();
    }

    public interface IImageSaveController { }

    public interface IApplicationStatusMediator : NINA.Core.Interfaces.IMediator<NINA.WPF.Base.Interfaces.ViewModel.IApplicationStatusVM> {
        void StatusUpdate(NINA.Core.Model.ApplicationStatus status);
    }

    public class BeforeFinalizeImageSavedEventArgs {
        public BeforeFinalizeImageSavedEventArgs(NINA.Image.Interfaces.IRenderedImage image) { Image = image; }
        public NINA.Image.Interfaces.IRenderedImage Image { get; }
        public System.Collections.ObjectModel.ReadOnlyCollection<NINA.Core.Model.ImagePattern> Patterns => new System.Collections.ObjectModel.ReadOnlyCollection<NINA.Core.Model.ImagePattern>(new System.Collections.Generic.List<NINA.Core.Model.ImagePattern>());
        public void AddImagePattern(NINA.Core.Model.ImagePattern p) { }
    }

    public class BeforeImageSavedEventArgs : System.EventArgs {
        public BeforeImageSavedEventArgs(NINA.Image.Interfaces.IImageData image, System.Threading.Tasks.Task<NINA.Image.Interfaces.IRenderedImage> prepareTask) { Image = image; ImagePrepareTask = prepareTask; }
        public NINA.Image.Interfaces.IImageData Image { get; }
        public System.Threading.Tasks.Task<NINA.Image.Interfaces.IRenderedImage> ImagePrepareTask { get; }
    }

    public class ImageSavedEventArgs : System.EventArgs {
        public NINA.Image.ImageData.ImageMetaData MetaData { get; set; }
        public System.Windows.Media.Imaging.BitmapSource Image { get; set; }
        public NINA.Image.Interfaces.IImageStatistics Statistics { get; set; }
        public NINA.Image.Interfaces.IStarDetectionAnalysis StarDetectionAnalysis { get; set; }
        public System.Uri PathToImage { get; set; }
        public NINA.Core.Enum.FileTypeEnum FileType { get; set; }
        public bool IsBayered { get; set; }
        public double Duration { get; set; }
        public string Filter { get; set; }
    }
}

namespace NINA.WPF.Base.Mediator {
    public interface IImageSaveMediator : NINA.WPF.Base.Interfaces.Mediator.IImageSaveMediator {
    }

    public class BeforeImageSavedEventArgs : NINA.WPF.Base.Interfaces.Mediator.BeforeImageSavedEventArgs {
        public BeforeImageSavedEventArgs(NINA.Image.Interfaces.IImageData image, System.Threading.Tasks.Task<NINA.Image.Interfaces.IRenderedImage> prepareTask)
            : base(image, prepareTask) {
        }
    }

    public class BeforeFinalizeImageSavedEventArgs : NINA.WPF.Base.Interfaces.Mediator.BeforeFinalizeImageSavedEventArgs {
        public BeforeFinalizeImageSavedEventArgs(NINA.Image.Interfaces.IRenderedImage image)
            : base(image) {
        }
    }

    public class ImageSavedEventArgs : NINA.WPF.Base.Interfaces.Mediator.ImageSavedEventArgs {
    }
}

namespace NINA.WPF.Base.Model {
    public class ImageHistoryPoint { }
}

namespace NINA.WPF.Base.Utility.AutoFocus {
    public class AutoFocusReport { }
}

namespace NINA.WPF.Base.ViewModel {
    public class PlateSolvingStatusVM : NINA.Core.Utility.BaseINPC {
        public PlateSolvingStatusVM() {
            PlateSolveHistory = new NINA.Core.Utility.AsyncObservableCollection<NINA.PlateSolving.PlateSolveResult>();
            Progress = new System.Progress<NINA.PlateSolving.PlateSolveProgress>();
        }
        public NINA.Core.Utility.AsyncObservableCollection<NINA.PlateSolving.PlateSolveResult> PlateSolveHistory { get; }
        public System.IProgress<NINA.PlateSolving.PlateSolveProgress> Progress { get; }
        public System.IProgress<NINA.Core.Model.ApplicationStatus> CreateLinkedProgress(System.IProgress<NINA.Core.Model.ApplicationStatus> original) => original;
        public NINA.Core.Model.ApplicationStatus Status { get; set; }
        public NINA.PlateSolving.PlateSolveResult PlateSolveResult { get; set; }
        public System.Windows.Media.Imaging.BitmapSource Thumbnail { get; set; }
    }
}

#endif
