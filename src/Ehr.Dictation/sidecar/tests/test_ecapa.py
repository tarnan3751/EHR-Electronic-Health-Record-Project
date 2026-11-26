"""ecapa.py: the port's network has SpeechBrain's keys and shapes, weights load only when they are the manifest's file
and fit every key and shape (weights_only, strict), and the features and embeddings come out as diarize.py takes them.
No model is needed: the network is built with random weights, and the file tests use a small one."""

from __future__ import annotations

import hashlib
import io
import pickle
from pathlib import Path

import numpy as np
import pytest
import torch
from conftest import tone

from ehr_dictation import ecapa
from ehr_dictation.audio import SAMPLE_RATE

PUBLISHED_BYTES = 83_316_686  # embedding_model.ckpt at the pinned commit (setup_models.py)


def small(monkeypatch) -> None:
    """A network of the same shape, much narrower, so its file is small."""
    monkeypatch.setattr(ecapa, "CHANNELS", (32, 32, 32, 32, 96))
    monkeypatch.setattr(ecapa, "SE_CHANNELS", 8)
    monkeypatch.setattr(ecapa, "ATTENTION_CHANNELS", 8)
    monkeypatch.setattr(ecapa, "EMBEDDING_SIZE", 16)


def saved(tmp_path, state: dict) -> tuple:
    buffer = io.BytesIO()
    torch.save(state, buffer)
    path = tmp_path / "embedding_model.ckpt"
    path.write_bytes(buffer.getvalue())
    return path, hashlib.sha256(buffer.getvalue()).hexdigest()


def test_the_network_has_speechbrains_keys_and_shapes():
    state = ecapa._network()().state_dict()

    assert state["blocks.0.conv.conv.weight"].shape == (1024, 80, 5)
    assert state["blocks.1.res2net_block.blocks.6.conv.conv.weight"].shape == (128, 128, 3)
    assert state["blocks.3.se_block.conv1.conv.weight"].shape == (128, 1024, 1)
    assert state["mfa.conv.conv.weight"].shape == (3072, 3072, 1)
    assert state["asp.tdnn.conv.conv.weight"].shape == (128, 9216, 1)
    assert state["asp_bn.norm.running_var"].shape == (6144,)
    assert state["fc.conv.weight"].shape == (192, 6144, 1)
    assert len(state) == 231
    # The tensors alone are nearly all of the published checkpoint: the rest is the file's own index.
    tensors = sum(value.numel() * value.element_size() for value in state.values())
    assert PUBLISHED_BYTES - 200_000 < tensors < PUBLISHED_BYTES


def test_weights_load_when_they_are_the_manifests_and_fit(tmp_path, monkeypatch):
    small(monkeypatch)
    state = ecapa._network()().state_dict()
    path, sha256 = saved(tmp_path, state)

    model = ecapa.Ecapa(path, sha256)

    assert all(torch.equal(model._model.state_dict()[key], value) for key, value in state.items())
    assert not model._model.training


def test_weights_that_arent_the_manifests_are_refused_before_they_are_read(tmp_path, monkeypatch):
    small(monkeypatch)
    path, _ = saved(tmp_path, ecapa._network()().state_dict())

    with pytest.raises(ecapa.WeightsChanged, match="--verify"):
        ecapa.Ecapa(path, "0" * 64)


@pytest.mark.parametrize("change", ["missing", "extra", "shape"])
def test_weights_that_dont_fit_every_key_and_shape_are_refused(tmp_path, monkeypatch, change):
    small(monkeypatch)
    state = ecapa._network()().state_dict()
    if change == "missing":
        del state["fc.conv.bias"]
    elif change == "extra":
        state["classifier.weight"] = torch.zeros(2)
    else:
        state["fc.conv.bias"] = torch.zeros(17)
    path, sha256 = saved(tmp_path, state)

    with pytest.raises(ecapa.WeightsDontFit, match="doesn't fit"):
        ecapa.Ecapa(path, sha256)


class Payload:
    """Not a tensor: what a pickle that runs code would carry."""

    def __reduce__(self):
        return (print, ("this would have run",))


def test_a_file_holding_anything_but_tensors_is_never_unpickled(tmp_path, monkeypatch, capsys):
    small(monkeypatch)
    state = ecapa._network()().state_dict() | {"payload": Payload()}
    buffer = io.BytesIO()
    torch.save(state, buffer, pickle_module=pickle)
    path = tmp_path / "embedding_model.ckpt"
    path.write_bytes(buffer.getvalue())

    with pytest.raises(ecapa.WeightsDontFit, match="doesn't fit"):
        ecapa.Ecapa(path, hashlib.sha256(buffer.getvalue()).hexdigest())
    assert "this would have run" not in capsys.readouterr().out


def test_the_filterbank_is_speechbrains_triangles_on_the_mel_scale():
    filters = ecapa.filterbank()

    assert filters.shape == (201, 80) and filters.min() == 0
    # Every band takes something; the low ones are narrower than the FFT's 40 Hz bins, so they peak below 1.
    assert np.all(filters.max(axis=0) > 0) and np.all(filters[:, 40:].max(axis=0) > 0.75)
    peaks = filters.argmax(axis=0)
    assert np.all(np.diff(peaks) >= 0) and peaks[0] <= 2 and peaks[-1] >= 190


def test_features_are_80_bands_a_frame_with_each_windows_mean_taken_off(tmp_path, monkeypatch):
    small(monkeypatch)
    path, sha256 = saved(tmp_path, ecapa._network()().state_dict())
    model = ecapa.Ecapa(path, sha256)
    windows = torch.from_numpy(np.stack([tone(1.5, frequency=300.0), tone(1.5, frequency=2000.0)]))

    features = model.features(windows)

    assert features.shape == (2, 1 + int(1.5 * SAMPLE_RATE) // ecapa.HOP_LENGTH, 80)
    assert torch.allclose(features.mean(dim=1), torch.zeros(2, 80), atol=1e-3)
    assert features[0].mean(dim=0).argmax() < features[1].mean(dim=0).argmax(), "a higher tone, a higher band"


def test_embed_gives_one_embedding_a_window_the_same_however_they_are_batched(tmp_path, monkeypatch):
    small(monkeypatch)
    path, sha256 = saved(tmp_path, ecapa._network()().state_dict())
    model = ecapa.Ecapa(path, sha256)
    windows = np.stack([tone(1.5, frequency=200.0 + 40 * i) for i in range(ecapa.BATCH + 3)])

    together = model.embed(windows)
    alone = model.embed(windows[-1:])

    assert together.shape == (ecapa.BATCH + 3, 16)
    assert np.allclose(together[-1], alone[0], atol=1e-4)
    assert model.embed(windows[:0]).shape == (0, 16)


# Series 1's review, F10: the Apache License's full text goes wherever the port goes (its section 4(a)).
def test_the_licenses_full_text_is_beside_the_port():
    text = (Path(ecapa.__file__).parent / "LICENSE-SpeechBrain.md").read_text(encoding="utf-8")

    assert "Apache License" in text and "Version 2.0, January 2004" in text
    assert "END OF TERMS AND CONDITIONS" in text and "4. Redistribution." in text
