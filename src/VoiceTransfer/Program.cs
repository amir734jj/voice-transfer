using CommandLine;
using Serilog;
using VoiceTransfer;
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

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var parser = new Parser(settings =>
    {
        settings.HelpWriter = Console.Error;
        settings.CaseInsensitiveEnumValues = true;
    });

    parser.ParseArguments<SendOptions, ReceiveOptions, LiveSendOptions, LiveReceiveOptions>(args)
        .WithParsed<SendOptions>(opts =>
        {
            SenderMode.Run(opts.InputFile, opts.OutputWav, opts.DeviceIndex, opts.BuildProfile(), opts.Password);
        })
        .WithParsed<ReceiveOptions>(opts =>
        {
            ReceiverMode.Run(opts.OutputFile, opts.InputWav, opts.DeviceIndex, opts.TimeoutSeconds, opts.BuildProfile(), opts.Password);
        })
        .WithParsed<LiveSendOptions>(opts =>
        {
            InteractiveSender.Run(opts.DeviceIndex, opts.BuildProfile(), opts.Password);
        })
        .WithParsed<LiveReceiveOptions>(opts =>
        {
            InteractiveReceiver.Run(opts.DeviceIndex, opts.OutputDeviceIndex, opts.BuildProfile(), opts.Loopback, opts.Password);
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
