"""Negative release-gate checks using temporary NuGet fixtures."""
import importlib.util
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile

spec = importlib.util.spec_from_file_location("validator", Path(__file__).with_name("validate-packages.py"))
validator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(validator)


class PackageValidationTests(unittest.TestCase):
    def create(self, directory, missing=None, dependency_version="0.1.0-preview.1", secret=False,
               dependency_id="Redis.Iris.AgentMemory"):
        for package_id in validator.EXPECTED - {missing}:
            with ZipFile(Path(directory) / f"{package_id}.nupkg", "w") as archive:
                archive.writestr(f"{package_id}.nuspec", f'''<package><metadata>
<id>{package_id}</id><version>0.1.0-preview.1</version><dependencies><group>
<dependency id="{dependency_id}" version="{dependency_version}" />
</group></dependencies></metadata></package>''')
                archive.writestr(f"lib/net8.0/{package_id}.dll", b"test-secret" if secret else b"fixture")

    def test_complete_release(self):
        with tempfile.TemporaryDirectory() as directory:
            self.create(directory)
            self.assertEqual("0.1.0-preview.1", validator.validate(directory))

    def test_each_missing_package_is_rejected(self):
        for package_id in validator.EXPECTED:
            with self.subTest(package_id=package_id), tempfile.TemporaryDirectory() as directory:
                self.create(directory, missing=package_id)
                with self.assertRaisesRegex(ValueError, "Missing release packages"):
                    validator.validate(directory)

    def test_management_cannot_be_a_transitive_dependency(self):
        with tempfile.TemporaryDirectory() as directory:
            self.create(directory, dependency_id="Redis.Iris.ContextRetriever.Management")
            with self.assertRaisesRegex(ValueError, "Management must remain optional"):
                validator.validate(directory)

    def test_wrong_dependency_and_secret_are_rejected(self):
        for options, message in [({"dependency_version": "0.0.1"}, "Dependency version mismatch"),
                                 ({"secret": True}, "Potential test credential")]:
            with self.subTest(options=options), tempfile.TemporaryDirectory() as directory:
                self.create(directory, **options)
                with self.assertRaisesRegex(ValueError, message):
                    validator.validate(directory)


if __name__ == "__main__":
    unittest.main()
