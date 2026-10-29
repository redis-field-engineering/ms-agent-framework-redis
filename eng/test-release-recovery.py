"""Synthetic release recovery tests: no feed requests or real package pushes."""
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

spec = importlib.util.spec_from_file_location("release", Path(__file__).with_name("release-packages.py"))
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
COMMIT = "a" * 40
VERSION = "0.1.0-preview.1"


def package(package_id, commit=COMMIT, payload=b"synthetic"):
    data = io.BytesIO()
    with ZipFile(data, "w") as archive:
        archive.writestr(f"{package_id}.nuspec", f'''<package><metadata>
<id>{package_id}</id><version>{VERSION}</version><repository commit="{commit}" />
</metadata></package>''')
        archive.writestr(f"lib/net8.0/{package_id}.dll", payload)
    return data.getvalue()


class RecoveryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.files = {}
        for name in release.ORDER:
            data = package(name)
            self.files[name] = data
            (self.directory / f"{name}.{VERSION}.nupkg").write_bytes(data)
        release.write_manifest(self.directory, COMMIT)

    def test_partial_release_skips_only_matching_signed_content(self):
        signed = io.BytesIO(self.files[release.ORDER[0]])
        with ZipFile(signed, "a") as archive:
            archive.writestr(".signature.p7s", b"synthetic repository signature")
        plan = release.plan_release(self.directory, COMMIT,
                                    lambda name, _: signed.getvalue() if name == release.ORDER[0] else None)
        pushed = []
        release.publish_plan(plan, pushed.append)
        self.assertEqual([f"{name}.{VERSION}.nupkg" for name in release.ORDER[1:]], [p.name for p in pushed])

    def test_already_complete_release_needs_no_push(self):
        plan = release.plan_release(self.directory, COMMIT, lambda name, _: self.files[name])
        release.publish_plan(plan, lambda _: self.fail("Unexpected push"))

    def test_conflicting_remote_content_blocks_all_publication(self):
        with self.assertRaisesRegex(ValueError, "conflicts"):
            release.plan_release(self.directory, COMMIT,
                                 lambda name, _: package(name, payload=b"different") if name == release.ORDER[-1] else None)

    def test_changed_artifact_or_commit_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "commit mismatch"):
            release.verify_manifest(self.directory, "b" * 40)
        path = next(self.directory.glob("*.nupkg"))
        path.write_bytes(path.read_bytes() + b"changed zip bytes")
        with self.assertRaisesRegex(ValueError, "manifest"):
            release.verify_manifest(self.directory, COMMIT)

    def test_network_failure_is_not_treated_as_absence(self):
        with patch.object(release.urllib.request, "urlopen", side_effect=release.urllib.error.URLError("offline")):
            with self.assertRaisesRegex(RuntimeError, "unavailable"):
                release.plan_release(self.directory, COMMIT)

    def test_failed_push_stops_before_dependents(self):
        plan = release.plan_release(self.directory, COMMIT, lambda *_: None)
        attempted = []
        def push(path):
            attempted.append(path.name)
            raise RuntimeError("Synthetic failure")
        with self.assertRaisesRegex(RuntimeError, "Synthetic failure"):
            release.publish_plan(plan, push)
        self.assertEqual([f"{release.ORDER[0]}.{VERSION}.nupkg"], attempted)

    def test_push_failure_does_not_print_key_or_child_output(self):
        with patch.dict(release.os.environ, {"NUGET_API_KEY": "synthetic-sensitive-key"}), \
             patch.object(release.subprocess, "run", return_value=release.subprocess.CompletedProcess([], 1, b"synthetic-sensitive-key", b"private detail")):
            with self.assertRaises(RuntimeError) as error:
                release.push_package(Path("fixture.nupkg"))
            self.assertNotIn("synthetic-sensitive-key", str(error.exception))
            self.assertNotIn("private detail", str(error.exception))


if __name__ == "__main__":
    unittest.main()
