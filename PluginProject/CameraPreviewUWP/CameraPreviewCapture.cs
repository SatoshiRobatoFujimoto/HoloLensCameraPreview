﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Foundation.Collections;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;

namespace CameraPreview
{
    public delegate void OnVideoCaptureResourceCreatedCallback(CameraPreviewCapture captureObject);
    public delegate void OnVideoModeStartedCallback(VideoCaptureResult result);
    public delegate void FrameSampleAcquiredCallback(VideoCaptureSample videoCaptureSample);
    public delegate void OnVideoModeStoppedCallback(VideoCaptureResult result);

    public sealed class CameraPreviewCapture
    {
        public event FrameSampleAcquiredCallback FrameSampleAcquired;

        public bool IsStreaming {
            get {
                return _frameReader != null;
            }
        }

        static readonly MediaStreamType STREAM_TYPE = MediaStreamType.VideoPreview;
        static readonly Guid ROTATION_KEY = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");

        // MediaFrameSourceGroup _frameSourceGroup;
        MediaFrameSourceInfo _frameSourceInfo;
        // DeviceInformation _deviceInfo;
        MediaCapture _mediaCapture;
        MediaFrameReader _frameReader;

        CameraPreviewCapture(MediaFrameSourceInfo frameSourceInfo)
        {
            _frameSourceInfo = frameSourceInfo;
        }

        public static async void CreateAync(OnVideoCaptureResourceCreatedCallback onCreatedCallback)
        {
            var allFrameSourceGroups = await MediaFrameSourceGroup.FindAllAsync();                                              //Returns IReadOnlyList<MediaFrameSourceGroup>
            var candidateFrameSourceGroups = allFrameSourceGroups.Where(group =>
                group.SourceInfos.Any(sourceInfo => 
                    sourceInfo.MediaStreamType == MediaStreamType.VideoPreview && 
                    sourceInfo.SourceKind == MediaFrameSourceKind.Color
                )
            );   //Returns IEnumerable<MediaFrameSourceGroup>

            var selectedFrameSourceGroup = candidateFrameSourceGroups.FirstOrDefault();                                         //Returns a single MediaFrameSourceGroup

            if (selectedFrameSourceGroup == null)
            {
                onCreatedCallback?.Invoke(null);
                return;
            }

            var selectedFrameSourceInfo = selectedFrameSourceGroup.SourceInfos.FirstOrDefault(); //Returns a MediaFrameSourceInfo

            if (selectedFrameSourceInfo == null)
            {
                onCreatedCallback?.Invoke(null);
                return;
            }

            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);   //Returns DeviceCollection
            var deviceInformation = devices.FirstOrDefault();                               //Returns a single DeviceInformation

            if (deviceInformation == null)
            {
                onCreatedCallback?.Invoke(null);
                return;
            }

            var videoCapture = new CameraPreviewCapture(selectedFrameSourceInfo);
   
            var result = await videoCapture.CreateMediaCaptureAsync(selectedFrameSourceGroup, deviceInformation);
            if (result)
            {
                onCreatedCallback?.Invoke(videoCapture);
            } else
            {
                onCreatedCallback?.Invoke(null);
            }
        }

        public IEnumerable<Resolution> GetSupportedResolutions()
        {
            List<Resolution> resolutions = new List<Resolution>();

            var allPropertySets = _mediaCapture.VideoDeviceController.GetAvailableMediaStreamProperties(STREAM_TYPE).Select(x => x as VideoEncodingProperties); //Returns IEnumerable<VideoEncodingProperties>
            foreach (var propertySet in allPropertySets)
            {
                resolutions.Add(new Resolution((int)propertySet.Width, (int)propertySet.Height));
            }

            return resolutions.AsReadOnly();
        }

        public IEnumerable<float> GetSupportedFrameRatesForResolution(Resolution resolution)
        {
            //Get all property sets that match the supported resolution
            var allPropertySets = _mediaCapture.VideoDeviceController.GetAvailableMediaStreamProperties(STREAM_TYPE).Select((x) => x as VideoEncodingProperties)
                .Where((x) =>
                {
                    return x != null &&
                    x.Width == (uint)resolution.width &&
                    x.Height == (uint)resolution.height;
                }); //Returns IEnumerable<VideoEncodingProperties>

            //Get all resolutions without duplicates.
            var frameRatesDict = new Dictionary<float, bool>();
            foreach (var propertySet in allPropertySets)
            {
                if (propertySet.FrameRate.Denominator != 0)
                {
                    float frameRate = (float)propertySet.FrameRate.Numerator / (float)propertySet.FrameRate.Denominator;
                    frameRatesDict.Add(frameRate, true);
                }
            }

            //Format resolutions as a list.
            var frameRates = new List<float>();
            foreach (KeyValuePair<float, bool> kvp in frameRatesDict)
            {
                frameRates.Add(kvp.Key);
            }

            return frameRates.AsReadOnly();
        }

        async Task<bool> CreateMediaCaptureAsync(MediaFrameSourceGroup _frameSourceGroup, DeviceInformation _deviceInfo)
        {
            if (_mediaCapture != null)
            {
                throw new Exception("The MediaCapture object has already been created.");
            }

            _mediaCapture = new MediaCapture();

            var settings = new MediaCaptureInitializationSettings()
            {
                VideoDeviceId = _deviceInfo.Id,
                SourceGroup = _frameSourceGroup,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu, //TODO: Should this be the other option, Auto? GPU is not an option.
                StreamingCaptureMode = StreamingCaptureMode.Video
            };

            try
            {
                await _mediaCapture.InitializeAsync(settings);
                _mediaCapture.VideoDeviceController.Focus.TrySetAuto(true);
                return true;
            } catch(Exception ex)
            {
                _mediaCapture.Dispose();
                _mediaCapture = null;
                return false;
            }
        }

        public async void StartVideoModeAsync(bool IsCapturedHologram)
        {
            var mediaFrameSource = _mediaCapture.FrameSources[_frameSourceInfo.Id]; //Returns a MediaFrameSource

            if (mediaFrameSource == null)
            {
                return;
            }

            var pixelFormat= MediaEncodingSubtypes.Bgra8;

            _frameReader = await _mediaCapture.CreateFrameReaderAsync(mediaFrameSource, pixelFormat);

            _frameReader.FrameArrived += HandleFrameArrived;

            var result = await _frameReader.StartAsync();

            var allPropertySets = _mediaCapture.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview)
                .Select(x => x as VideoEncodingProperties)
                .Where(x =>
                {
                    if (x == null) return false;
                    if (x.FrameRate.Denominator == 0) return false;

                    double frameRate = (double)x.FrameRate.Numerator / (double)x.FrameRate.Denominator;

                    return x.Width == 896 && x.Height == 504 && (int)Math.Round(frameRate) == 30;
                });

            VideoEncodingProperties properties = allPropertySets.FirstOrDefault();

            //	gr: taken from here https://forums.hololens.com/discussion/2009/mixedrealitycapture
            IVideoEffectDefinition ved = new VideoMRCSettings(IsCapturedHologram, false, 0, 0.0f);
            await _mediaCapture.AddVideoEffectAsync(ved, MediaStreamType.VideoPreview);
            await _mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(STREAM_TYPE, properties);

        }

        public async void StopVideoModeAsync(OnVideoModeStoppedCallback onVideoModeStoppedCallback)
        {
            if (IsStreaming == false)
            {
                onVideoModeStoppedCallback?.Invoke(new VideoCaptureResult(1, ResultType.InappropriateState, false));
                return;
            }

            _frameReader.FrameArrived -= HandleFrameArrived;
            await _frameReader.StopAsync();
            _frameReader.Dispose();
            _frameReader = null;

            onVideoModeStoppedCallback?.Invoke(new VideoCaptureResult(0, ResultType.Success, true));
        }

        public void Dispose()
        {
            if (IsStreaming)
            {
                throw new Exception("Please make sure StopVideoModeAsync() is called before displosing the VideoCapture object.");
            }

            _mediaCapture?.Dispose();
        }

        void HandleFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            if (FrameSampleAcquired == null)
            {
                return;
            }

            using (var frameReference = _frameReader.TryAcquireLatestFrame()) //frameReference is a MediaFrameReference
            {
                if (frameReference != null)
                {
                    var sample = new VideoCaptureSample(frameReference);
                    FrameSampleAcquired?.Invoke(sample);
                }
            }
        }

        VideoEncodingProperties GetVideoEncodingPropertiesForCameraParams(CameraParameters cameraParams)
        {
            var allPropertySets = _mediaCapture.VideoDeviceController.GetAvailableMediaStreamProperties(STREAM_TYPE).Select((x) => x as VideoEncodingProperties)
                .Where((x) =>
                {
                    if (x == null) return false;
                    if (x.FrameRate.Denominator == 0) return false;

                    double calculatedFrameRate = (double)x.FrameRate.Numerator / (double)x.FrameRate.Denominator;

                    return
                    x.Width == (uint)cameraParams.cameraResolutionWidth &&
                    x.Height == (uint)cameraParams.cameraResolutionHeight &&
                    (int)Math.Round(calculatedFrameRate) == cameraParams.frameRate;
                }); //Returns IEnumerable<VideoEncodingProperties>

            if (allPropertySets.Count() == 0)
            {
                throw new Exception("Could not find an encoding property set that matches the given camera parameters.");
            }

            var chosenPropertySet = allPropertySets.FirstOrDefault();
            return chosenPropertySet;
        }

        static string ConvertCapturePixelFormatToMediaEncodingSubtype(CapturePixelFormat format)
        {
            switch (format)
            {
                case CapturePixelFormat.BGRA32:
                    return MediaEncodingSubtypes.Bgra8;
                case CapturePixelFormat.NV12:
                    return MediaEncodingSubtypes.Nv12;
                case CapturePixelFormat.JPEG:
                    return MediaEncodingSubtypes.Jpeg;
                case CapturePixelFormat.PNG:
                    return MediaEncodingSubtypes.Png;
                default:
                    return MediaEncodingSubtypes.Bgra8;
            }
        }
    }

    public class VideoMRCSettings : IVideoEffectDefinition
    {
        public string ActivatableClassId {
            get {
                return "Windows.Media.MixedRealityCapture.MixedRealityCaptureVideoEffect";
            }
        }

        public IPropertySet Properties {
            get; private set;
        }

        public VideoMRCSettings(bool HologramCompositionEnabled, bool VideoStabilizationEnabled, int VideoStabilizationBufferLength, float GlobalOpacityCoefficient)
        {
            Properties = (IPropertySet)new PropertySet();
            Properties.Add("HologramCompositionEnabled", HologramCompositionEnabled);
            Properties.Add("VideoStabilizationEnabled", VideoStabilizationEnabled);
            Properties.Add("VideoStabilizationBufferLength", VideoStabilizationBufferLength);
            Properties.Add("GlobalOpacityCoefficient", GlobalOpacityCoefficient);
        }
    }
}