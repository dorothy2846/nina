#region "copyright"

/*
    Copyright (c) 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Image.FileFormat;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.WPF.Base.Interfaces.Mediator {

    public interface IApplicationMediator : IMediator<IApplicationVM> {

        void ChangeTab(ApplicationTab tab);
    }

    public interface IApplicationStatusMediator : IMediator<IApplicationStatusVM> {

        void StatusUpdate(ApplicationStatus status);
    }

    public interface IImageSaveMediator : IMediator<IImageSaveController> {

        Task Enqueue(IImageData imageData, Task<IRenderedImage> prepareTask, IProgress<ApplicationStatus> progress, CancellationToken token);
        event Func<object, BeforeImageSavedEventArgs, Task> BeforeImageSaved;
        event Func<object, BeforeFinalizeImageSavedEventArgs, Task> BeforeFinalizeImageSaved;
        event EventHandler<ImageSavedEventArgs> ImageSaved;

        void Shutdown();
    }

    public class BeforeFinalizeImageSavedEventArgs {

        public BeforeFinalizeImageSavedEventArgs(IRenderedImage image) {
            Image = image;
        }

        public IRenderedImage Image { get; }
    }

    public class BeforeImageSavedEventArgs : EventArgs {
        public BeforeImageSavedEventArgs(IImageData image, Task<IRenderedImage> prepareTask) {
            Image = image;
            ImagePrepareTask = prepareTask;
        }

        public IImageData Image { get; }
        public Task<IRenderedImage> ImagePrepareTask { get; }
    }

    public class ImageSavedEventArgs : EventArgs {
        public ImageMetaData MetaData { get; set; }
        public object Image { get; set; }
        public IImageStatistics Statistics { get; set; }
        public IStarDetectionAnalysis StarDetectionAnalysis { get; set; }
        public Uri PathToImage { get; set; }
        public FileTypeEnum FileType { get; set; }
        public bool IsBayered { get; set; }
        public double Duration { get; set; }
        public string Filter { get; set; }
    }
}

namespace NINA.WPF.Base.Interfaces.ViewModel {

    using NINA.WPF.Base.Interfaces.Mediator;

    public interface IApplicationVM {

        void ChangeTab(ApplicationTab tab);
    }

    public interface IApplicationStatusVM {

        void StatusUpdate(ApplicationStatus status);
    }

    public interface IImageSaveController {

        Task Enqueue(IImageData imageData, Task<IRenderedImage> prepareTask, IProgress<ApplicationStatus> progress, CancellationToken token);
        void Shutdown();

        event Func<object, BeforeImageSavedEventArgs, Task> BeforeImageSaved;
        event Func<object, BeforeFinalizeImageSavedEventArgs, Task> BeforeFinalizeImageSaved;
        event EventHandler<ImageSavedEventArgs> ImageSaved;
    }
}
