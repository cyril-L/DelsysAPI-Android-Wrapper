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

/*
 * Design choices
 *
 * When generating wrappers, Embbedinator seems to strip the type of generic
 * collections (ArrayList instead of ArrayList<String>). This forces to cast
 * everything on the Java side. Because of this:
 * - List are exposed as arrays (string[] instead of List<string>)
 * - Data is exposed with objects (ComponentInfo instead of Dictionnary<string>
 *
 * Calling Java from C# is a bit messy
 * https://docs.microsoft.com/en-us/xamarin/tools/dotnet-embedding/android/callbacks
 * Because of this the interface have made synchronous. For example instead
 * of Scan() + onScanComplete(result), a blocking Scan() returning the result
 * is exposed. All the methods exposed are blocking.
 *
 * To allow the client to use the data storage it wants, a ByteBuffer is used
 * to store the result. Allowing the client to use a direct ByteBuffer if he
 * wants to process the data in native code, or a double[] if processed in Java
 * For now the client is responsible of computing the correct capacity of the
 * buffer.
 *
 * The C# API looks more complex than necessary for the user. Only a trivial
 * interface is exposed to Java for now.
 */

// TODO query battery BTPipeline.TrignoBtManager.QueryBatteryComponentAsync(comp).Result
// TODO expose as Singleton
// TODO get raw data?

namespace DelsysAndroidWrapper
{
    [Register("fr.trinoma.daq.delsys.androidwrapper.DelsysApiWrapper")]
    public class DelsysApiWrapper : Java.Lang.Object
    {
        Pipeline BTPipeline;

        #region Initialization

        [Export("initialize")]
        public void Initialize(string publicKey, string license)
        {
            var deviceSourceCreator = new DelsysAPI.Android.DeviceSourcePortable(publicKey, license);
            deviceSourceCreator.SetDebugOutputStream(Console.WriteLine);
            var source = deviceSourceCreator.GetDataSource(SourceType.TRIGNO_BT);

            PipelineController.Instance.AddPipeline(source);
            BTPipeline = PipelineController.Instance.PipelineIds[0];

            BTPipeline.CollectionDataReady += CollectionDataReady;
        }

        #endregion

        #region Scanning devices

        [Export("scan")]
        public bool Scan()
        {
            var task = BTPipeline.Scan();
            task.Wait();
            return task.Result;
        }

        [Export("listDevices")]
        public ComponentInfo[] ListDevices()
        {
            var devices = new ComponentInfo[BTPipeline.TrignoBtManager.Components.Count];
            var i = 0;
            foreach (var component in BTPipeline.TrignoBtManager.Components)
            {
                // TODO just after scan component Serial Number and Type is not
                // filled. When is it populated?
                devices[i++] = new ComponentInfo {
                    id = component.Id.ToString(),
                    name = component.Name,
                    serial = component.Properties.SerialNumber,
                    sensorType = component.Properties.SensorType,
                    modes = component.SensorConfiguration.SampleModes
                };
            }
            return devices;
        }

        #endregion

        #region Pipeline configuration

        [Export("arm")]
        public ChanelInfo[] Arm(JavaList<ComponentConfig> componentConfigs)
        {
            // This sequence have been extracted from the Android BT example
            // from Delsys. I don’t really understand why it is that convoluted,
            // but I kept it like this for now.

            foreach (var config in componentConfigs)
            {
                var component = getComponentById(config.componentId);
                if (component == null) {
                    Console.WriteLine("Unable to find component {0}", config.componentId);
                    return null;
                }
                if (Array.IndexOf(component.SensorConfiguration.SampleModes, config.mode) < 0)
                {
                    Console.WriteLine("Component {0} does not supports mode {1}", config.componentId, config.mode);
                    return null;
                }
                component.SensorConfiguration.SelectSampleMode(config.mode);
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

            var channelsInfo = new ChanelInfo[numChannels];

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
                        channelsInfo[channelIndex] = new ChanelInfo {
                            componentId = component.Id.ToString(),
                            samplesPerFrame = channel.SamplesPerFrame,
                            frameInterval = channel.FrameInterval,
                            unit = channel.Unit.ToString(),
                        };
                        channelIndex++;
                    }
                }
            }

            if (!BTPipeline.ApplyOutputConfigurations(outconfig)) {
                return null;
            }

            BTPipeline.RunTime = Double.MaxValue;
            return channelsInfo;
        }

        private SensorTrignoBt getComponentById(string id)
        {
            foreach (var component in BTPipeline.TrignoBtManager.Components)
            {
                if (component.Id.ToString() == id)
                {
                    return component;
                }
            }
            return null;
        }

        [Export("disarm")]
        public bool Disarm()
        {
            var task = BTPipeline.DisarmPipeline();
            task.Wait();
            return task.Result;
        }

        #endregion

        #region Streaming data to a Java ByteBuffer

        [Export("start")]
        public bool Start()
        {
            var task = BTPipeline.Start();
            task.Wait();
            return task.Result;
        }

        private readonly object outputBufferLock = new object();
        private readonly AutoResetEvent outputBufferWritten = new AutoResetEvent(false);
        private Java.Nio.ByteBuffer outputBuffer;

        [Export("read")]
        public bool Read(Java.Nio.ByteBuffer output)
        {
            lock (outputBufferLock) {
                outputBuffer = output;
            }
            while (!outputBufferWritten.WaitOne(100)) {
                if (BTPipeline.CurrentState != Pipeline.ProcessState.Running) {
                    return false;
                }
            }
            return true;
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

        [Export("stop")]
        public bool Stop()
        {
            var task = BTPipeline.Stop();
            task.Wait();
            return task.Result;
        }

        [Export("getState")]
        public Pipeline.ProcessState GetState()
        {
            return BTPipeline.CurrentState;
        }

        #endregion
    }

    #region Structured objects used to expose information to the Java user

    [Register("fr.trinoma.daq.delsys.androidwrapper.ComponentInfo")]
    public class ComponentInfo : Java.Lang.Object
    {
        public string id;
        public string name;
        public string serial;
        public string sensorType;
        public string[] modes;

        [Export("getId")]
        public string GetId() {
            return id;
        }

        [Export("getName")]
        public string GetName() {
            return name;
        }

        [Export("getSerial")]
        public string GetSerial()  {
            return serial;
        }

        [Export("getSensorType")]
        public string GetSensorType() {
            return sensorType;
        }

        [Export("getModes")]
        public string[] GetModes() {
            return modes;
        }
    }

    [Register("fr.trinoma.daq.delsys.androidwrapper.ChannelInfo")]
    public class ChanelInfo : Java.Lang.Object
    {
        public string componentId;
        public int samplesPerFrame;
        public double frameInterval;
        public string unit;

        [Export("getComponentId")]
        public string GetComponentId()
        {
            return componentId;
        }

        [Export("getSamplesPerFrame")]
        public int GetSamplesPerFrame()
        {
            return samplesPerFrame;
        }

        [Export("getFrameInterval")]
        public double GetFrameInterval()
        {
            return frameInterval;
        }

        [Export("getUnit")]
        public string GetUnit()
        {
            return unit;
        }
    }

    [Register("fr.trinoma.daq.delsys.androidwrapper.ComponentConfig")]
    public class ComponentConfig : Java.Lang.Object
    {
        public string componentId;
        public string mode;

        private ComponentConfig() { }

        // TODO I got an error at runtime when using this constructor in Java
        // android.runtime.JavaProxyThrowable: System.NotSupportedException:
        // Don't know how to convert type 'System.String' to an Android.Runtime.IJavaObject.
        //public ComponentConfig(string componentId, string mode)
        //{
        //    this.componentId = componentId;
        //    this.mode = mode;
        //}
        //
        // Used a factory method instead
        [Export("create")]
        public static ComponentConfig Create(string componentId, string mode) {
            return new ComponentConfig()
            {
                componentId = componentId,
                mode = mode
            };
        }

        [Export("getComponentId")]
        public string GetComponentId()
        {
            return componentId;
        }

        [Export("getMode")]
        public string GetMode()
        {
            return mode;
        }
    }

    #endregion
}
