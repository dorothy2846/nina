#region "copyright"

/*
    Copyright � 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces.Mediator;
using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Astrometry;
using NINA.Core.Utility.Extensions;
using NINA.Core.Interfaces;
using NINA.Equipment.Equipment.MyGuider.PHD2;

namespace NINA.WPF.Base.Mediator {

    public class GuiderMediator : DeviceMediator<IGuiderVM, IGuiderConsumer, GuiderInfo>, IGuiderMediator {

        public Task<bool> Dither(CancellationToken token) {
            if (handler == null) {
                return Task.FromResult(true);
            }
            return handler.Dither(token);
        }

        public Guid StartRMSRecording() {
            return handler?.StartRMSRecording() ?? Guid.Empty;
        }

        public RMS GetRMSRecording(Guid handle) {
            return handler?.GetRMSRecording(handle) ?? new RMS();
        }

        public RMS StopRMSRecording(Guid handle) {
            return handler?.StopRMSRecording(handle) ?? new RMS();
        }

        public Task<bool> StartGuiding(bool forceCalibration, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (handler == null) {
                return Task.FromResult(true);
            }
            return handler.StartGuiding(forceCalibration, progress, token);
        }

        public Task<bool> StopGuiding(CancellationToken token) {
            if (handler == null) {
                return Task.FromResult(true);
            }
            return handler.StopGuiding(token);
        }

        public Task<bool> AutoSelectGuideStar(CancellationToken token) {
            if (handler == null) {
                return Task.FromResult(true);
            }
            return handler.AutoSelectGuideStar(token);
        }

        public Task<bool> ClearCalibration(CancellationToken token) {
            if (handler == null) {
                return Task.FromResult(true);
            }
            return handler.ClearCalibration(token);
        }

        public Task<bool> SetShiftRate(SiderealShiftTrackingRate shiftTrackingRate, CancellationToken ct) {
            if (handler == null) {
                return Task.FromResult(true);
            }
            return handler.SetShiftRate(shiftTrackingRate, ct);
        }

        public Task<bool> StopShifting(CancellationToken ct) {
            if (handler == null) {
                return Task.FromResult(true);
            }
            return handler.StopShifting(ct);
        }

        public LockPosition GetLockPosition() {
            return handler?.GetLockPosition() ?? null!;
        }

        private event EventHandler<IGuideStep> _guideEvent;
        private event Func<object, EventArgs, Task> _afterDither;
        private event Func<object, EventArgs, Task> _guidingStarted;
        private event Func<object, EventArgs, Task> _guidingStopped;

        /// <summary>
        /// Will be raised each time the application receives guide pulse info from the guider
        /// </summary>
        public event EventHandler<IGuideStep> GuideEvent {
            add { if (handler != null) handler.GuideEvent += value; else _guideEvent += value; }
            remove { if (handler != null) handler.GuideEvent -= value; else _guideEvent -= value; }
        }

        public event Func<object, EventArgs, Task> AfterDither {
            add { if (handler != null) handler.AfterDither += value; else _afterDither += value; }
            remove { if (handler != null) handler.AfterDither -= value; else _afterDither -= value; }
        }

        public event Func<object, EventArgs, Task> GuidingStarted {
            add { if (handler != null) handler.GuidingStarted += value; else _guidingStarted += value; }
            remove { if (handler != null) handler.GuidingStarted -= value; else _guidingStarted -= value; }
        }

        public event Func<object, EventArgs, Task> GuidingStopped {
            add { if (handler != null) handler.GuidingStopped += value; else _guidingStopped += value; }
            remove { if (handler != null) handler.GuidingStopped -= value; else _guidingStopped -= value; }
        }
    }
}
