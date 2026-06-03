# VoiceTransfer

Covert data-over-voice transmission tool. Uses FSK (Frequency-Shift Keying) modulation to embed data in audio that sounds like background modem/line noise. Two frequencies within the telephone passband carry binary data; the Goertzel algorithm (a single-bin DFT) separates the narrowband FSK signal from broadband human voice.

## Features

- **File transfer over audio** — send any file as FSK tones through speakers/microphone or WAV files
- **Live interactive mode** — type text lines and transmit them in real-time; receiver prints decoded text as it arrives
- **AES-256-GCM encryption** — optional password-based authenticated encryption
- **Forward Error Correction** — configurable bit-repetition coding with majority voting (1x/3x/5x+)
- **Stealth shaping** — pink noise mixing, amplitude wobble, soft onset/offset, and spectral spreading disguise the signal as phone line static
- **Voice passthrough** — in live-receive mode, human voice audio passes through to speakers while FSK data is decoded in parallel
- **Speed presets** — `slow` (150 baud), `normal` (300 baud), `fast` (350 baud), with full parameter override

## Usage

### Send a file

```bash
# Play through speakers
VoiceTransfer send -f secret.txt

# Write to WAV file instead
VoiceTransfer send -f secret.txt -o output.wav
```

### Receive a file

```bash
# Listen from microphone
VoiceTransfer receive -o received.txt

# Decode from WAV file
VoiceTransfer receive -o received.txt -i output.wav
```

### Live interactive mode

```bash
# Sender: type lines to transmit
VoiceTransfer live-send

# Receiver: listen and print decoded text, pass voice to speakers
VoiceTransfer live-receive
```

### Encryption

Add `--password` to any command. Sender and receiver must use the same password.

```bash
VoiceTransfer send -f secret.txt --password mypassword
VoiceTransfer receive -o received.txt --password mypassword
```

### Speed presets

```bash
# Slow (150 baud) — most noise-resilient
VoiceTransfer send -f data.bin -p slow

# Normal (300 baud) — default, good balance
VoiceTransfer send -f data.bin -p normal

# Fast (350 baud) — needs cleaner signal
VoiceTransfer send -f data.bin -p fast
```

### Parameter overrides

All transmission parameters can be individually overridden. Sender and receiver must match.

```bash
VoiceTransfer send -f data.bin --baud 200 --fec 5 --amplitude 0.2
VoiceTransfer receive -o data.bin --baud 200 --fec 5
```

## How it works

1. **Frame encoding** — input data is base64-encoded, optionally encrypted (AES-256-GCM), wrapped in a frame with preamble, sync byte, length header, and CRC-16
2. **FEC encoding** — each bit is repeated N times for error correction via majority voting
3. **FSK modulation** — bits are converted to audio tones at mark (2200 Hz) and space (1800 Hz) frequencies
4. **Stealth shaping** — the pure tones are mixed with pink noise and shaped to resemble phone line interference
5. **Transmission** — audio plays through speakers or is written to a WAV file
6. **Reception** — the Goertzel algorithm detects FSK energy in narrow frequency bins, rejecting broadband voice
7. **FEC decoding** — majority voting corrects bit errors
8. **Frame decoding** — CRC verification, decryption, and file reconstruction

## License

Private.
