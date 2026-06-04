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

    using var parser = new Parser(settings =>
    {
        settings.HelpWriter = Console.Error;
        settings.CaseInsensitiveEnumValues = true;
    });

    parser.ParseArguments<SendOptions, ReceiveOptions, LiveSendOptions, LiveReceiveOptions, TestOptions, RecoverOptions>(args)
        .WithParsed<SendOptions>(opts =>
        {
            var audio = AudioBackendFactory.Create(opts.AudioEngine);
            var device = DeviceResolver.ResolveOutput(audio, opts.DeviceIndex);
            SenderMode.Run(audio, opts.InputFile, opts.OutputWav, device, opts.BuildProfile(), opts.Password);
        })
        .WithParsed<ReceiveOptions>(opts =>
        {
            var audio = AudioBackendFactory.Create(opts.AudioEngine);
            var device = DeviceResolver.ResolveInput(audio, opts.DeviceIndex);
            ReceiverMode.Run(audio, opts.OutputFile, opts.InputWav, device, opts.TimeoutSeconds, opts.BuildProfile(), opts.Password);
        })
        .WithParsed<LiveSendOptions>(opts =>
        {
            var audio = AudioBackendFactory.Create(opts.AudioEngine);
            var device = DeviceResolver.ResolveOutput(audio, opts.DeviceIndex);
            InteractiveSender.Run(audio, device, opts.BuildProfile(), opts.Password);
        })
        .WithParsed<LiveReceiveOptions>(opts =>
        {
            var audio = AudioBackendFactory.Create(opts.AudioEngine, opts.Loopback);
            var inputDevice = DeviceResolver.ResolveInput(audio, opts.DeviceIndex);
            var outputDevice = DeviceResolver.ResolveOutput(audio, opts.OutputDeviceIndex);
            InteractiveReceiver.Run(audio, inputDevice, outputDevice, opts.BuildProfile(), opts.Loopback, opts.Passthrough, opts.Password);
        })
        .WithParsed<TestOptions>(opts =>
        {
            var audio = AudioBackendFactory.Create(opts.AudioEngine);
            var inputDevice = DeviceResolver.ResolveInput(audio, opts.DeviceIndex);
            var outputDevice = DeviceResolver.ResolveOutput(audio, opts.OutputDeviceIndex);
            TestMode.Run(audio, inputDevice, outputDevice, opts.Duration);
        })
        .WithParsed<RecoverOptions>(opts =>
        {
            RecoverMode.Run(opts.InputWav, opts.OutputFile, opts.Password);
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

        Log.Information("Update downloaded, applying and exiting");
        mgr.WaitExitThenApplyUpdates(newVersion.TargetFullRelease, silent: true, restart: true, restartArgs: args);
        Environment.Exit(0);
    }
    catch (Exception ex)
    {
        Log.Debug(ex, "Update check skipped");
    }
}
