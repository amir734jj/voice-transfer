using CommandLine;
using Serilog;
using Velopack;
using Velopack.Sources;
using VoiceTransfer.Audio;
using VoiceTransfer.Commands;
using VoiceTransfer.Modes;

// -----------------------------------------------------------
// VoiceTransfer -- Covert data-over-voice transmission tool
//
// Uses FSK (Frequency-Shift Keying) modulation to embed data
// in audio that sounds like background modem noise.
// Two frequencies within the telephone passband carry binary
// data; the Goertzel algorithm (a single-bin DFT) separates
// the narrowband FSK signal from broadband human voice.
// -----------------------------------------------------------

VelopackApp.Build().Run();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    await CheckForUpdates();

    var parser = new Parser(settings =>
    {
        settings.HelpWriter = Console.Error;
        settings.CaseInsensitiveEnumValues = true;
    });

    parser.ParseArguments<SendOptions, ReceiveOptions, LiveSendOptions, LiveReceiveOptions, TestOptions>(args)
        .WithParsed<SendOptions>(opts =>
        {
            var audio = AudioBackendFactory.Create(opts.AudioEngine);
            SenderMode.Run(audio, opts.InputFile, opts.OutputWav, opts.DeviceIndex, opts.BuildProfile(), opts.Password);
        })
        .WithParsed<ReceiveOptions>(opts =>
        {
            var audio = AudioBackendFactory.Create(opts.AudioEngine);
            ReceiverMode.Run(audio, opts.OutputFile, opts.InputWav, opts.DeviceIndex, opts.TimeoutSeconds, opts.BuildProfile(), opts.Password);
        })
        .WithParsed<LiveSendOptions>(opts =>
        {
            var audio = AudioBackendFactory.Create(opts.AudioEngine);
            InteractiveSender.Run(audio, opts.DeviceIndex, opts.BuildProfile(), opts.Password);
        })
        .WithParsed<LiveReceiveOptions>(opts =>
        {
            var audio = AudioBackendFactory.Create(opts.AudioEngine, opts.Loopback);
            InteractiveReceiver.Run(audio, opts.DeviceIndex, opts.OutputDeviceIndex, opts.BuildProfile(), opts.Loopback, opts.Password);
        })
        .WithParsed<TestOptions>(opts =>
        {
            var audio = AudioBackendFactory.Create(opts.AudioEngine);
            TestMode.Run(audio, opts.DeviceIndex, opts.OutputDeviceIndex, opts.Duration);
        })
        .WithNotParsed(_ => { }); // CommandLineParser already prints help
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled exception");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

async Task CheckForUpdates()
{
    try
    {
        var mgr = new UpdateManager(new GithubSource("https://github.com/amir734jj/voice-transfer", null, false));
        if (!mgr.IsInstalled)
        {
            return;
        }

        var newVersion = await mgr.CheckForUpdatesAsync();
        if (newVersion == null)
        {
            return;
        }

        Log.Information("Downloading update v{Version}", newVersion.TargetFullRelease.Version);
        await mgr.DownloadUpdatesAsync(newVersion);

        Log.Information("Update downloaded, restarting");
        mgr.ApplyUpdatesAndRestart(newVersion.TargetFullRelease);
    }
    catch (Exception ex)
    {
        Log.Debug(ex, "Update check skipped");
    }
}
