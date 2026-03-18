#region "copyright"

/*
    Copyright � 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Astrometry;
using NINA.Equipment.Interfaces.Mediator;
using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Interfaces;
using NINA.Core.Utility.Extensions;

namespace NINA.WPF.Base.Mediator {

    public class TelescopeMediator : DeviceMediator<ITelescopeVM, ITelescopeConsumer, TelescopeInfo>, ITelescopeMediator {

        public void MoveAxis(TelescopeAxes axis, double rate) {
            handler?.MoveAxis(axis, rate);
        }

        public void PulseGuide(GuideDirections direction, int duration) {
            handler?.PulseGuide(direction, duration);
        }

        public async Task<bool> Sync(Coordinates coordinates) {
            if (handler == null) {
                return true;
            }
            return await handler.Sync(coordinates);
        }

        public Task<bool> SlewToCoordinatesAsync(Coordinates coords, CancellationToken token) {
            if (handler == null) {
                return Task.FromResult(true);
            }
            return handler.SlewToCoordinatesAsync(coords, token);
        }

        public Task<bool> SlewToCoordinatesAsync(TopocentricCoordinates coords, CancellationToken token) {
            if (handler == null) {
                return Task.FromResult(true);
            }
            return handler.SlewToCoordinatesAsync(coords, token);
        }

        public Task<bool> SlewToTopocentricCoordinates(TopocentricCoordinates coords, CancellationToken token) {
            if (handler == null) {
                return Task.FromResult(true);
            }
            return handler.SlewToTopocentricCoordinates(coords, token);
        }

        public Task<bool> MeridianFlip(Coordinates targetCoordinates, CancellationToken token) {
            if (handler == null) {
                return Task.FromResult(true);
            }
            return handler.MeridianFlip(targetCoordinates, token);
        }

        public bool SetTrackingEnabled(bool tracking) {
            return handler?.SetTrackingEnabled(tracking) ?? true;
        }

        public bool SendToSnapPort(bool start) {
            return handler?.SendToSnapPort(start) ?? true;
        }

        public Task<bool> ParkTelescope(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (handler == null) {
                return Task.FromResult(true);
            }
            return handler.ParkTelescope(progress, token);
        }

        public Task<bool> UnparkTelescope(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (handler == null) {
                return Task.FromResult(true);
            }
            return handler.UnparkTelescope(progress, token);
        }

        public Coordinates GetCurrentPosition() {
            return handler?.GetCurrentPosition() ?? new Coordinates(0d, 0d, Epoch.J2000, Coordinates.RAType.Hours);
        }

        public Task WaitForSlew(CancellationToken cancellationToken) {
            if (handler == null) {
                return Task.CompletedTask;
            }
            return handler.WaitForSlew(cancellationToken);
        }

        public bool SetTrackingMode(TrackingMode trackingMode) {
            return handler?.SetTrackingMode(trackingMode) ?? true;
        }

        public bool SetCustomTrackingRate(SiderealShiftTrackingRate rate) {
            return handler?.SetCustomTrackingRate(rate) ?? true;
        }

        public Task<bool> FindHome(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (handler == null) {
                return Task.FromResult(true);
            }
            return handler.FindHome(progress, token);
        }

        public void StopSlew() {
            handler?.StopSlew();
        }

        public PierSide DestinationSideOfPier(Coordinates coordinates) {
            return handler?.DestinationSideOfPier(coordinates) ?? PierSide.pierUnknown;
        }

        public event Func<object, BeforeMeridianFlipEventArgs, Task> BeforeMeridianFlip;

        public async Task RaiseBeforeMeridianFlip(BeforeMeridianFlipEventArgs e) {
            await (BeforeMeridianFlip?.InvokeAsync(this, e) ?? Task.CompletedTask);
        }

        public event Func<object, AfterMeridianFlipEventArgs, Task> AfterMeridianFlip;

        public async Task RaiseAfterMeridianFlip(AfterMeridianFlipEventArgs e) {
            await (AfterMeridianFlip?.InvokeAsync(this, e) ?? Task.CompletedTask);
        }
        private event Func<object, EventArgs, Task> _parked;
        private event Func<object, EventArgs, Task> _unparked;
        private event Func<object, EventArgs, Task> _homed;
        private event Func<object, MountSlewedEventArgs, Task> _slewed;

        public event Func<object, EventArgs, Task> Parked {
            add { if (handler != null) handler.Parked += value; else _parked += value; }
            remove { if (handler != null) handler.Parked -= value; else _parked -= value; }
        }
        public event Func<object, EventArgs, Task> Unparked {
            add { if (handler != null) handler.Unparked += value; else _unparked += value; }
            remove { if (handler != null) handler.Unparked -= value; else _unparked -= value; }
        }
        public event Func<object, EventArgs, Task> Homed {
            add { if (handler != null) handler.Homed += value; else _homed += value; }
            remove { if (handler != null) handler.Homed -= value; else _homed -= value; }
        }
        public event Func<object, MountSlewedEventArgs, Task> Slewed {
            add { if (handler != null) handler.Slewed += value; else _slewed += value; }
            remove { if (handler != null) handler.Slewed -= value; else _slewed -= value; }
        }
    }
}
