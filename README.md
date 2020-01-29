# Delsys API Android Wrapper

Allows to connect Delsys Electromyography (EMG) sensors to an Android phone.

Delsys provides a C# Xamarin example for Android to use its API, which can be found on the [Delsys API website](http://data.delsys.com/DelsysServicePortal/api/index.html). Note that a license file from Delsys is needed to use the API.

This project wraps the Xamarin example in an Android Library that can be used with Android Studio.

## Build

- Open and build the solution with Visual Studio (tested on macOS only)
- Generate an Android Library (.aar) with Embeddinator-4000

  ```
   mono './packages/Embeddinator-4000.0.4.0/tools/Embeddinator-4000.exe' './Sample/Sample.Android/bin/Debug/DelsysAndroidWrapper.dll' --gen=Java --platform=Android --outdir='./output' -c
   ```

- Get the Android Library in `output/DelsysAndroidWrapper.aar`

## Usage

- Import the .aar to your Android Studio project ([I did it like this](https://stackoverflow.com/a/34919810))
- Prevent to compress dll files in your app build.gradle

  ```gradle
  android {
      …
      aaptOptions {
          noCompress 'dll'
      }
  }
  ```

The following interface is now exposed to your Android app :

```java

public interface DelsysApiWrapper  {

    void initialize(String publicKey, String license);

    ComponentInfo[] scan();

    ComponentInfo[] listDevices();

    ChannelInfo[] arm(ArrayList<ComponentConfig> components);

    void start();

    void read(ByteBuffer buf);

    void stop();
}

public interface ComponentInfo {

    String getId();

    String getName();

    String getSerial();

    String getSensorType();

    String[] getModes();
}

public interface ComponentConfig {

    static ComponentConfig create(String componentId, String mode);

    String getComponentId();

    String getMode();
}

public interface ChannelInfo {

    String getComponentId();

    int getSamplesPerFrame();

    double getFrameInterval();

    String getUnit();
}
```