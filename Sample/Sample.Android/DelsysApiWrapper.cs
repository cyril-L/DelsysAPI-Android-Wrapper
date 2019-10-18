using System;

using Android.Runtime;
using DelsysAPI.Pipelines;
using DelsysAPI.DelsysDevices;
using DelsysAPI.Events;
using DelsysAPI.Utils.TrignoBt;
using DelsysAPI.Utils;
using DelsysAPI.Configurations.DataSource;
using DelsysAPI.Configurations;
using DelsysAPI.Transforms;
using DelsysAPI.Channels.Transform;
using Java.Interop;
using System.Threading;
using DelsysAPI.Components.TrignoBT;

// TODO query battery BTPipeline.TrignoBtManager.QueryBatteryComponentAsync(comp).Result

namespace DelsysAndroidWrapper
{
    [Register("fr.trinoma.daq.delsys.androidwrapper.DelsysApiWrapper")]
    public class DelsysApiWrapper : Java.Lang.Object
    {
        Pipeline BTPipeline;

        #region Button Events (Scan, Start, and Stop)

        [Export("start")]
        public void Start()
        {
            BTPipeline.Start().Wait();
        }

        [Export("scan")]
        public JavaList<JavaDictionary<string, string>> Scan()
        {
            BTPipeline.Scan().Wait();
            return ListDevices();
        }

        [Export("stop")]
        public void Stop()
        {
            BTPipeline.Stop().Wait();
        }

        #endregion

        #region Initialization

        [Export("initialize")]
        public void Initialize(string publicKey, string license)
        {
            var deviceSourceCreator = new DelsysAPI.Android.DeviceSourcePortable(publicKey, license);
            deviceSourceCreator.SetDebugOutputStream(Console.WriteLine);
            var source = deviceSourceCreator.GetDataSource(SourceType.TRIGNO_BT);
            // Here we use the key and license we previously loaded.
            source.Key = publicKey;
            source.License = license;

            PipelineController.Instance.AddPipeline(source);

            BTPipeline = PipelineController.Instance.PipelineIds[0];

            BTPipeline.CollectionDataReady += CollectionDataReady;

            // Other available callbacks CollectionStarted, CollectionComplete,
            // ComponentAdded, ComponentLost, ComponentRemoved, ComponentScanComplete
        }

        [Export("listDevices")]
        public JavaList<JavaDictionary<string, string>> ListDevices()
        {
            var devices = new JavaList<JavaDictionary<string, string>>();
            foreach (var component in BTPipeline.TrignoBtManager.Components)
            {
                // TODO just after scan component Serial Number and Type is not
                // filled. When is it populated?
                devices.Add(new JavaDictionary<string, string>() {
                    {"id", component.Id.ToString()},
                    {"name", component.Name},
                    {"serial", component.Properties.SerialNumber},
                    {"type", component.Properties.SensorType},
                    {"modes", String.Join(", ", component.SensorConfiguration.SampleModes)},
                });
            }
            return devices;
        }

        private SensorTrignoBt getComponentById(string id) {
            foreach (var component in BTPipeline.TrignoBtManager.Components)
            {
                if (component.Id.ToString() == id)
                {
                    return component;
                }
            }
            return null;
        }

        [Export("arm")]
        public JavaList<JavaDictionary<string, string>> Arm(JavaDictionary<string, JavaDictionary<string, string>> devicesConfig)
        {
            // This sequence have been extracted from the Android BT example
            // from Delsys. I don’t really understand why it is that convoluted,
            // but I kept it like this for now.

            if (BTPipeline.CurrentState == Pipeline.ProcessState.OutputsConfigured || BTPipeline.CurrentState == Pipeline.ProcessState.Armed)
            {
                BTPipeline.DisarmPipeline().Wait();
            }
            if (BTPipeline.CurrentState == Pipeline.ProcessState.Running)
            {
                BTPipeline.Stop().Wait();
            }

            foreach (var entry in devicesConfig)
            {
                var componentId = entry.Key;
                var component = getComponentById(entry.Key);
                if (component == null) {
                    Console.WriteLine("Unable to find component {0}", componentId);
                    return null;
                }
                string mode;
                if (!entry.Value.TryGetValue("mode", out mode)) {
                    Console.WriteLine("No mode provided for component {0}", componentId);
                    return null;
                }
                if (Array.IndexOf(component.SensorConfiguration.SampleModes, mode) < 0)
                {
                    Console.WriteLine("Component {0} does not supports mode {1}", componentId, mode);
                    return null;
                }
                component.SensorConfiguration.SelectSampleMode(mode);
                BTPipeline.TrignoBtManager.SelectComponentAsync(component).Wait();
            }

            BTPipeline.TrignoBtManager.Configuration = new TrignoBTConfig() { EOS = EmgOrSimulate.EMG };

            // Validates the number of sensors intended to be streamed.
            var inputConfiguration = new BTDsConfig();
            inputConfiguration.NumberOfSensors = BTPipeline.TrignoBtManager.Components.Count;
            BTPipeline.ApplyInputConfigurations(inputConfiguration);

            BTPipeline.TransformManager.TransformList.Clear();

            int numChannels = 0;
            foreach (var component in BTPipeline.TrignoBtManager.Components)
            {
                if (component.State == SelectionState.Allocated
                    && component.SensorConfiguration.IsComponentAvailable())
                {
                    numChannels += component.BtChannels.Count;
                }
            }

            // Create the raw data transform, with an input and output channel for every
            // channel that exists in our setup. This transform applies the scaling to the raw
            // data from the sensor.
            // TODO check if we can stream raw data instead
            var rawDataTransform = new TransformRawData(numChannels, numChannels);
            BTPipeline.TransformManager.AddTransform(rawDataTransform);

            var outconfig = new OutputConfig();
            outconfig.NumChannels = numChannels;

            var channelsInfo = new JavaList<JavaDictionary<string, string>>();

            int channelIndex = 0;
            foreach (var component in BTPipeline.TrignoBtManager.Components)
            {
                if (component.State == SelectionState.Allocated
                    && component.SensorConfiguration.IsComponentAvailable())
                {
                    foreach (var channel in component.BtChannels)
                    {
                        var chout = new ChannelTransform(channel.FrameInterval, channel.SamplesPerFrame, channel.Unit);
                        BTPipeline.TransformManager.AddInputChannel(rawDataTransform, channel);
                        BTPipeline.TransformManager.AddOutputChannel(rawDataTransform, chout);
                        // Channel index defines how is the data ordered when received on CollectionDataReady callback
                        outconfig.MapOutputChannel(channelIndex, chout);
                        channelsInfo.Add(new JavaDictionary<string, string>() {
                            {"component", component.Id.ToString()},
                            {"name", channel.Name},
                            {"samplesPerFrame", channel.SamplesPerFrame.ToString()},
                            {"unit", channel.Unit.ToString()},
                            {"frameInterval", channel.FrameInterval.ToString()},
                        });
                        channelIndex++;
                    }
                }
            }

            BTPipeline.ApplyOutputConfigurations(outconfig);
            BTPipeline.RunTime = Double.MaxValue;

            return channelsInfo;
        }

        #endregion

        #region Collection Callbacks -- Data Ready, Colleciton Started, and Collection Complete

        private readonly object outputBufferLock = new object();
        private readonly AutoResetEvent outputBufferWritten = new AutoResetEvent(false);
        private Java.Nio.ByteBuffer outputBuffer;

        [Export("read")]
        public void Read(Java.Nio.ByteBuffer output)
        {
            lock (outputBufferLock) {
                outputBuffer = output;
            }
            outputBufferWritten.WaitOne();
        }

        public void CollectionDataReady(object sender, ComponentDataReadyEventArgs e)
        {
            lock (outputBufferLock)
            {
                if (outputBuffer == null) {
                    Console.WriteLine("Nobody is reading, dropping frame");
                    return;
                }
                foreach (var channel in e.Data) {
                    if (outputBuffer.Remaining() < channel.Data.Count * sizeof(double)) {
                        Console.Write("Buffer underflow, dropping remaining data");
                        break;
                    }
                    for (int i = 0; i < channel.Data.Count; i++) {
                        if (!channel.IsLostData[i]) {
                            outputBuffer.PutDouble(channel.Data[i]);
                        } else {
                            outputBuffer.PutDouble(Double.NaN);
                        }
                    }
                }
                outputBuffer = null;
                outputBufferWritten.Set();
            }
        }
        #endregion
    }
}
