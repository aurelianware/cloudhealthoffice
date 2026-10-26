import base64
import importlib.util
from pathlib import Path
from unittest.mock import patch

import pytest

SCRIPT = Path(__file__).resolve().parents[2] / "scripts/demo/gcp/initialize_appeals_key.py"
spec = importlib.util.spec_from_file_location("initialize_appeals_key", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def test_initializes_empty_secret_through_stdin_only():
    with patch.object(module, "gcloud", side_effect=[b"[]", b"version-resource"]) as call:
        assert module.initialize("demo-project")
    payload = call.call_args.kwargs["payload"]
    assert len(base64.b64decode(payload)) == 32
    assert "--data-file=-" in call.call_args.args[0]
    assert payload.decode() not in " ".join(call.call_args.args[0])


def test_existing_versions_are_never_replaced():
    with patch.object(module, "gcloud", return_value=b'[{"name":"version-1","state":"DISABLED"}]') as call:
        assert not module.initialize("demo-project")
    assert call.call_count == 1


def test_lookup_failure_does_not_generate_or_publish_a_key():
    with patch.object(module, "gcloud", side_effect=RuntimeError("lookup failed")) as call:
        with pytest.raises(RuntimeError):
            module.initialize("demo-project")
    assert call.call_count == 1


def test_subprocess_errors_do_not_echo_payload_or_stderr():
    import subprocess
    result = subprocess.CompletedProcess([], 1, b"", b"private-key-material")
    with patch.object(module.subprocess, "run", return_value=result):
        with pytest.raises(RuntimeError, match="no key material") as error:
            module.gcloud(["secrets"], payload=b"private-key-material")
    assert "private-key-material" not in str(error.value)
