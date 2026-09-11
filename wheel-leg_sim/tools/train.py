"""Launcher for mlagents-learn that keeps the ONNX export working.

Use this instead of `mlagents-learn`. Every argument is passed straight through:

    python tools/train.py config/ppo.yaml --run-id=r1 --force \
        --env=C:\\wlsim_build\\sim.exe --no-graphics \
        --env-args --profile local --controller policy --areas 1 --out C:\\wlsim_build\\runs\\r1

Why this exists
---------------
mlagents 1.1.0 calls `torch.onnx.export(...)` with no `dynamo` argument. From PyTorch 2.9 that
public entry point routes to the torch.export-based exporter, which imports `onnxscript`.
Installing onnxscript pulls onnx >= 1.16 and protobuf >= 4, and protobuf 4+ refuses the
pre-generated `_pb2` modules mlagents ships:

    TypeError: Descriptors cannot be created directly.

So the trainer cannot talk to Unity at all any more. The two requirements are not satisfiable
in one environment.

They do not have to be. The legacy TorchScript exporter is still present and still works in
this PyTorch (torch/onnx/utils.py and every symbolic_opset*.py), it is only no longer the
default. Measured on torch 2.13.0+cpu with onnx 1.15.0 and protobuf 3.20.3, no onnxscript:

    torch.onnx.export(...)                 -> ModuleNotFoundError: onnxscript
    torch.onnx.export(..., dynamo=False)   -> 10548 bytes, correct tensor names
    torch.onnx.utils.export(...)           -> 10548 bytes, correct tensor names

Forcing `dynamo=False` therefore fixes the export without touching torch, onnx or protobuf,
and leaves the trainer <-> Unity path exactly as it was when it trained successfully.

Remove this shim when mlagents itself passes `dynamo=False`, or when the legacy exporter is
finally deleted from PyTorch and mlagents has moved on.
"""
import sys

import torch


def _force_legacy_onnx_exporter():
    original = torch.onnx.export

    def export(*args, **kwargs):
        # setdefault, not a hard override: a caller that deliberately asks for the new
        # exporter still gets it.
        kwargs.setdefault("dynamo", False)
        return original(*args, **kwargs)

    torch.onnx.export = export


def main():
    _force_legacy_onnx_exporter()
    # Imported after the patch so anything capturing the symbol at import time sees the shim.
    from mlagents.trainers.learn import main as learn_main
    learn_main()


if __name__ == "__main__":
    sys.exit(main())
