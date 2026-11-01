"""Builds assets/sounds/*.wav from the dog sounds (E:\\SPT Mod\\Assets\\Dog Sounds, first decoded to plain WAVs,
e.g. by Blender's audaspace, since there's no mp3 reader here): the barking track cut into its single barks
(bark_1.wav ...), the whimper and the shake, each cleaned of background noise, trimmed of silence and brought to the
same loudness; and the panting as breathing.wav, a seamless loop of whole breaths the dog breathes to all the time.
    python convert_sounds.py <decoded wav dir> <assets/sounds dir>"""
import os, sys, wave
import numpy as np

src, dst = sys.argv[1], sys.argv[2]
os.makedirs(dst, exist_ok=True)
TARGET, CEILING = .12, .95   # loudness (RMS of the part you hear) every sound is brought to, and the peak it can't pass


def read(name):
    with wave.open(os.path.join(src, name)) as w:
        rate = w.getframerate()
        data = np.frombuffer(w.readframes(w.getnframes()), '<i2').astype(np.float32) / 32768
    return data, rate


def level(data):
    """To the shared loudness: measured over the part that sounds (not the silences), peaks kept under the ceiling."""
    step = 441
    blocks = np.array([np.sqrt(np.mean(data[i:i + step] ** 2)) for i in range(0, len(data), step)])
    heard = blocks[blocks > blocks.max() * .15]
    gain = TARGET / max(np.sqrt(np.mean(heard ** 2)) if len(heard) else 1e-6, 1e-6)
    gain = min(gain, CEILING / max(np.abs(data).max(), 1e-6))
    return data * gain


def denoise(data, rate, strength=1.5, highpass=100):
    """Spectral noise gate: the background's spectrum is learnt from the quietest moments (between breaths or barks)
    and taken out of every moment, smoothed over time so it doesn't warble; rumble under `highpass` Hz goes too."""
    n, hop = 2048, 512
    window = np.hanning(n)
    pad = np.concatenate([np.zeros(n), data, np.zeros(n)])
    frames = [pad[i:i + n] * window for i in range(0, len(pad) - n, hop)]
    spectra = np.array([np.fft.rfft(f) for f in frames])
    mags = np.abs(spectra)
    energy = mags.sum(axis=1)
    quiet = mags[energy <= np.percentile(energy, 20)]
    noise = np.percentile(quiet, 60, axis=0) if len(quiet) else np.zeros(mags.shape[1])
    mask = np.clip(1 - strength * noise / np.maximum(mags, 1e-9), .03, 1)
    # Smoothed across neighbouring moments, so faint parts don't flicker in and out.
    smooth = mask.copy()
    for k in (1, 2):
        smooth[k:] += mask[:-k]; smooth[:-k] += mask[k:]
    mask = smooth / 5
    freqs = np.fft.rfftfreq(n, 1 / rate)
    mask[:, freqs < highpass] = 0
    out = np.zeros(len(pad)); norm = np.zeros(len(pad))
    for idx, i in enumerate(range(0, len(pad) - n, hop)):
        out[i:i + n] += np.fft.irfft(spectra[idx] * mask[idx], n) * window
        norm[i:i + n] += window ** 2
    out = out / np.maximum(norm, 1e-6)
    return out[n:n + len(data)].astype(np.float32)


def breathing_loop(data, rate, breaths=4, fade=.15):
    """A loop of whole breaths: the breath rhythm from the loudness's autocorrelation, cut between two breaths, and its
    end crossfaded into its start so it repeats without a seam."""
    step = rate // 100
    env = np.array([np.sqrt(np.mean(data[i:i + step] ** 2)) for i in range(0, len(data) - step, step)])
    e = env - env.mean(); ac = np.correlate(e, e, 'full')[len(e) - 1:]; ac /= max(ac[0], 1e-9)
    period = (20 + int(np.argmax(ac[20:120]))) * step
    length = breaths * period
    f = int(fade * rate)
    # Start at the quietest point of the first breath's gap.
    first = int(np.argmin(env[10:10 + period // step])) + 10
    start = first * step
    if start + length + f > len(data): start = max(0, len(data) - length - f)
    seg = data[start:start + length + f].astype(np.float32)
    out = seg[:length].copy()
    t = np.linspace(0, np.pi / 2, f)
    out[:f] = out[:f] * np.sin(t) + seg[length:length + f] * np.cos(t)   # equal-power crossfade of the end into the start
    print('breathing    period %.2f s, %d breaths' % (period / rate, breaths))
    return out


def write(name, data, rate):
    data = level(data)
    with wave.open(os.path.join(dst, name), 'wb') as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
        w.writeframes((data * 32767).astype('<i2').tobytes())
    print('%-12s %.2f s' % (name, len(data) / rate))


def loudness(data, rate, step=.01):
    n = int(rate * step)
    return np.array([np.sqrt(np.mean(data[i:i + n] ** 2)) for i in range(0, len(data), n)]), n


def trim(data, rate, floor=.04, pad=.03):
    rms, n = loudness(data, rate)
    on = np.where(rms > rms.max() * floor)[0]
    if len(on) == 0: return data
    a = max(0, on[0] * n - int(pad * rate)); b = min(len(data), (on[-1] + 1) * n + int(pad * rate))
    out = data[a:b].copy()
    fade = min(len(out) // 4, int(.01 * rate))     # no clicks at the cut
    out[:fade] *= np.linspace(0, 1, fade); out[-fade:] *= np.linspace(1, 0, fade)
    return out


def segments(data, rate, floor=.15, gap=.06, shortest=.12, pad=.04):
    """The loud stretches (a bark each), split where it's quiet for longer than gap."""
    rms, n = loudness(data, rate)
    loud = rms > rms.max() * floor
    found, start, quiet = [], None, 0
    for i, on in enumerate(loud):
        if on:
            if start is None: start = i
            quiet = 0
        elif start is not None:
            quiet += 1
            if quiet * .01 > gap: found.append((start, i - quiet + 1)); start, quiet = None, 0
    if start is not None: found.append((start, len(loud)))
    out = []
    for a, b in found:
        if (b - a) * .01 < shortest: continue
        x = data[max(0, a * n - int(pad * rate)):min(len(data), b * n + int(pad * rate))].copy()
        fade = int(.008 * rate); x[:fade] *= np.linspace(0, 1, fade); x[-fade:] *= np.linspace(1, 0, fade)
        out.append(x)
    return out


files = {f.lower(): f for f in os.listdir(src) if f.lower().endswith('.wav')}
def find(word): return next(f for k, f in files.items() if word in k)

data, rate = read(find('bark'))
for i, bark in enumerate(segments(denoise(data, rate, 1.2), rate), 1): write('bark_%d.wav' % i, bark, rate)
data, rate = read(find('whimper')); write('whine.wav', trim(denoise(data, rate, 1.3), rate), rate)
data, rate = read(find('panting')); write('breathing.wav', breathing_loop(denoise(data, rate, 2.0, 120), rate), rate)
data, rate = read(find('shak')); write('shake.wav', trim(denoise(data, rate, 1.2), rate), rate)
