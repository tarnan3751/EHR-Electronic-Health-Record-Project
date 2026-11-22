"""The speaker-embedding model that tells the voices of a recorded visit apart (the expansion plan's step 16): ECAPA-TDNN
with SpeechBrain's 80-band filterbank features, as speechbrain/spkrec-ecapa-voxceleb defines and trained it, run with
the sidecar's own torch and no other library. diarize.py uses it through its Embedder protocol.

The weights are setup_models.py's: the file models-manifest.json records, read once, hashed, and refused unless the
hash is the manifest's, then loaded from those same bytes with torch.load(weights_only=True), which unpickles tensors
and nothing else, into the network below with strict=True: every key the network has must be in the file with its
shape, and nothing else may be. So a file from another model, or another version of this one, never runs. The model
turns a passage of audio into 192 numbers that are near for one voice and far for two; they live in memory while the
voices are told apart and are never stored (no voiceprint is kept).

Ported from SpeechBrain (https://github.com/speechbrain/speechbrain), whose ECAPA_TDNN.py (speechbrain/lobes/models,
credited to Hwidong Na, 2020) and feature code (speechbrain/lobes/features.py and speechbrain/processing/features.py,
credited to Mirco Ravanelli and others, 2020) are Copyright their authors and licensed under the Apache License,
Version 2.0. You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0. Unless required by
applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language
governing permissions and limitations under the License. Its full text is LICENSE-SpeechBrain.md, beside this file
and shipped with it.

Changed from SpeechBrain's code: only what inference with these weights uses is kept (no training, dropout, grouped
convolutions, length masks or channel options; every window is a whole one), the layers are plain torch modules named
as SpeechBrain names them so the checkpoint's keys load as they are, the filterbank is built once from the same formulas,
and the sentence mean normalization and the batching are written here.
"""

from __future__ import annotations

import hashlib
import io
import math
import pickle
import threading
from pathlib import Path

import numpy as np

from .audio import SAMPLE_RATE

# The architecture of speechbrain/spkrec-ecapa-voxceleb (its hyperparams.yaml).
N_MELS = 80
CHANNELS = (1024, 1024, 1024, 1024, 3072)
KERNEL_SIZES = (5, 3, 3, 3, 1)
DILATIONS = (1, 2, 3, 4, 1)
ATTENTION_CHANNELS = 128
RES2NET_SCALE = 8
SE_CHANNELS = 128
EMBEDDING_SIZE = 192

# SpeechBrain's Fbank defaults: 25 ms Hamming windows every 10 ms, a 400-point FFT, power spectrum, log in dB with an
# 80 dB floor below each window's loudest band.
N_FFT = 400
WIN_LENGTH = 400
HOP_LENGTH = 160
TOP_DB = 80.0
AMIN = 1e-10

BATCH = 32  # windows a forward pass at once


class EcapaError(Exception):
    """The weights aren't the ones the manifest records, or don't fit the network."""


class WeightsChanged(EcapaError):
    """The file isn't the one models-manifest.json records: the service stops, as it does for MedASR."""


class WeightsDontFit(EcapaError):
    """The recorded file doesn't fit this port of the network: listening to a visit is off; dictation goes on."""


def _network():  # noqa: ANN202 (torch is imported here, so the module loads without it for the tests that fake it)
    import torch
    from torch import nn
    from torch.nn import functional

    class Conv1d(nn.Module):
        """SpeechBrain's Conv1d: "same" padding by reflection, then the convolution, kept as .conv."""

        def __init__(self, in_channels: int, out_channels: int, kernel_size: int, dilation: int = 1) -> None:
            super().__init__()
            self.padding = dilation * (kernel_size - 1) // 2
            self.conv = nn.Conv1d(in_channels, out_channels, kernel_size, dilation=dilation)

        def forward(self, x):  # noqa: ANN001, ANN202
            if self.padding:
                x = functional.pad(x, (self.padding, self.padding), mode="reflect")
            return self.conv(x)

    class BatchNorm1d(nn.Module):
        def __init__(self, size: int) -> None:
            super().__init__()
            self.norm = nn.BatchNorm1d(size)

        def forward(self, x):  # noqa: ANN001, ANN202
            return self.norm(x)

    class TDNNBlock(nn.Module):
        def __init__(self, in_channels: int, out_channels: int, kernel_size: int, dilation: int) -> None:
            super().__init__()
            self.conv = Conv1d(in_channels, out_channels, kernel_size, dilation)
            self.activation = nn.ReLU()
            self.norm = BatchNorm1d(out_channels)

        def forward(self, x):  # noqa: ANN001, ANN202
            return self.norm(self.activation(self.conv(x)))

    class Res2NetBlock(nn.Module):
        def __init__(self, channels: int, scale: int, kernel_size: int, dilation: int) -> None:
            super().__init__()
            width = channels // scale
            self.blocks = nn.ModuleList([TDNNBlock(width, width, kernel_size, dilation) for _ in range(scale - 1)])
            self.scale = scale

        def forward(self, x):  # noqa: ANN001, ANN202
            out = []
            for i, part in enumerate(torch.chunk(x, self.scale, dim=1)):
                if i == 0:
                    y = part
                elif i == 1:
                    y = self.blocks[i - 1](part)
                else:
                    y = self.blocks[i - 1](part + y)
                out.append(y)
            return torch.cat(out, dim=1)

    class SEBlock(nn.Module):
        def __init__(self, channels: int, se_channels: int) -> None:
            super().__init__()
            self.conv1 = Conv1d(channels, se_channels, 1)
            self.relu = nn.ReLU()
            self.conv2 = Conv1d(se_channels, channels, 1)
            self.sigmoid = nn.Sigmoid()

        def forward(self, x):  # noqa: ANN001, ANN202
            s = x.mean(dim=2, keepdim=True)
            return self.sigmoid(self.conv2(self.relu(self.conv1(s)))) * x

    class SERes2NetBlock(nn.Module):
        def __init__(self, channels: int, kernel_size: int, dilation: int) -> None:
            super().__init__()
            self.tdnn1 = TDNNBlock(channels, channels, 1, 1)
            self.res2net_block = Res2NetBlock(channels, RES2NET_SCALE, kernel_size, dilation)
            self.tdnn2 = TDNNBlock(channels, channels, 1, 1)
            self.se_block = SEBlock(channels, SE_CHANNELS)

        def forward(self, x):  # noqa: ANN001, ANN202
            return self.se_block(self.tdnn2(self.res2net_block(self.tdnn1(x)))) + x

    class AttentiveStatisticsPooling(nn.Module):
        """The attention-weighted mean and standard deviation over time, with the whole window's mean and standard
        deviation as context (global_context)."""

        eps = 1e-12

        def __init__(self, channels: int, attention_channels: int) -> None:
            super().__init__()
            self.tdnn = TDNNBlock(channels * 3, attention_channels, 1, 1)
            self.tanh = nn.Tanh()
            self.conv = Conv1d(attention_channels, channels, 1)

        def statistics(self, x, weights):  # noqa: ANN001, ANN202
            mean = (weights * x).sum(dim=2)
            std = torch.sqrt((weights * (x - mean.unsqueeze(2)).pow(2)).sum(dim=2).clamp(self.eps))
            return mean, std

        def forward(self, x):  # noqa: ANN001, ANN202
            frames = x.shape[-1]
            mean, std = self.statistics(x, torch.full_like(x[:, :1, :], 1.0 / frames))
            context = torch.cat([x, mean.unsqueeze(2).repeat(1, 1, frames), std.unsqueeze(2).repeat(1, 1, frames)], dim=1)
            attention = torch.softmax(self.conv(self.tanh(self.tdnn(context))), dim=2)
            mean, std = self.statistics(x, attention)
            return torch.cat((mean, std), dim=1).unsqueeze(2)

    class EcapaTdnn(nn.Module):
        def __init__(self) -> None:
            super().__init__()
            self.blocks = nn.ModuleList([TDNNBlock(N_MELS, CHANNELS[0], KERNEL_SIZES[0], DILATIONS[0])])
            for i in range(1, len(CHANNELS) - 1):
                self.blocks.append(SERes2NetBlock(CHANNELS[i], KERNEL_SIZES[i], DILATIONS[i]))
            self.mfa = TDNNBlock(CHANNELS[-2] * (len(CHANNELS) - 2), CHANNELS[-1], KERNEL_SIZES[-1], DILATIONS[-1])
            self.asp = AttentiveStatisticsPooling(CHANNELS[-1], ATTENTION_CHANNELS)
            self.asp_bn = BatchNorm1d(CHANNELS[-1] * 2)
            self.fc = Conv1d(CHANNELS[-1] * 2, EMBEDDING_SIZE, 1)

        def forward(self, features):  # noqa: ANN001, ANN202
            """Features (windows, frames, bands) in, embeddings (windows, 192) out."""
            x = features.transpose(1, 2)
            layers = []
            for block in self.blocks:
                x = block(x)
                layers.append(x)
            x = self.mfa(torch.cat(layers[1:], dim=1))
            return self.fc(self.asp_bn(self.asp(x))).squeeze(2)

    return EcapaTdnn


def filterbank() -> np.ndarray:
    """SpeechBrain's 80 triangular filters on the mel scale from 0 to 8 kHz, as a (frequency bins, bands) matrix: each
    filter's centre on the mel scale, and its half-width the distance from the centre below it."""
    def to_mel(hz: float) -> float:
        return 2595 * math.log10(1 + hz / 700)

    mel = np.linspace(to_mel(0), to_mel(SAMPLE_RATE / 2), N_MELS + 2)
    hz = 700 * (10 ** (mel / 2595) - 1)
    band = (hz[1:] - hz[:-1])[:-1]
    centre = hz[1:-1]
    frequencies = np.linspace(0, SAMPLE_RATE // 2, N_FFT // 2 + 1)
    slope = (frequencies[None, :] - centre[:, None]) / band[:, None]
    return np.maximum(0.0, np.minimum(slope + 1.0, -slope + 1.0)).T.astype(np.float32)


class Ecapa:
    """One copy of the model, shared by every visit; embed() takes a batch of windows at a time."""

    def __init__(self, path: Path, sha256: str, device: str = "cpu") -> None:
        import torch

        data = path.read_bytes()
        if hashlib.sha256(data).hexdigest() != sha256:
            raise WeightsChanged(f"{path.name} doesn't match models-manifest.json: run src/Ehr.Dictation/setup_models.py --verify.")
        model = _network()()
        try:
            model.load_state_dict(torch.load(io.BytesIO(data), map_location="cpu", weights_only=True), strict=True)
        except (RuntimeError, pickle.UnpicklingError) as error:
            raise WeightsDontFit(f"{path.name} doesn't fit the ECAPA-TDNN network: {error}") from None
        self._torch = torch
        self._device = device
        self._model = model.to(device).eval()
        self._filters = torch.from_numpy(filterbank()).to(device)
        self._window = torch.hamming_window(WIN_LENGTH).to(device)
        self._lock = threading.Lock()

    def features(self, windows):  # noqa: ANN001, ANN201
        """SpeechBrain's Fbank, then each window's mean over time taken off (its sentence normalization)."""
        torch = self._torch
        spectrum = torch.stft(windows, N_FFT, HOP_LENGTH, WIN_LENGTH, self._window, center=True, pad_mode="constant",
                              normalized=False, onesided=True, return_complex=True)
        power = spectrum.real.pow(2) + spectrum.imag.pow(2)
        bands = torch.matmul(power.transpose(1, 2), self._filters)
        decibels = 10 * torch.log10(torch.clamp(bands, min=AMIN))
        floor = decibels.amax(dim=(-2, -1)) - TOP_DB
        decibels = torch.maximum(decibels, floor.view(-1, 1, 1))
        return decibels - decibels.mean(dim=1, keepdim=True)

    def embed(self, windows: np.ndarray) -> np.ndarray:
        """Windows of 16 kHz samples, all the same length (windows, samples), to their embeddings (windows, 192)."""
        torch = self._torch
        out = []
        with self._lock, torch.inference_mode():
            for start in range(0, len(windows), BATCH):
                batch = torch.from_numpy(np.ascontiguousarray(windows[start : start + BATCH], dtype=np.float32))
                out.append(self._model(self.features(batch.to(self._device))).float().cpu().numpy())
        return np.concatenate(out) if out else np.zeros((0, EMBEDDING_SIZE), np.float32)
