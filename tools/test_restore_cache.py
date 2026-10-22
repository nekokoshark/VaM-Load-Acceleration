"""Recovery regressions for generated caches, file pairs and interrupted transactions."""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
SOURCE = Path(os.environ.get("VAMACC_TEST_INSTALLER", ROOT / "package/VaMAcceleration/tools/installer.py"))
spec = importlib.util.spec_from_file_location("cache_restore_installer", SOURCE)
assert spec and spec.loader
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class RestoreCacheTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory(prefix="restore 中文 space-")
        self.addCleanup(temporary.cleanup)
        self.game = Path(temporary.name) / "game"
        self.game.mkdir()
        self.cache = Path(temporary.name) / "external cache 中文"
        self.cache.mkdir()
        self.mri = self.cache / "VaMMemory/MorphImports" / ("imp-" + "AB" * 32 + ".mri")
        self.mri.parent.mkdir(parents=True)
        self.tx = installer.Transaction(self.game, self.cache, self.game / "VaMAcceleration/backups/20261023-000000")
        stopped = patch.object(installer, "game_stopped")
        stopped.start()
        self.addCleanup(stopped.stop)

    def state(self) -> dict:
        return installer.json_read(self.tx.state_path)

    def test_changed_morph_import_is_archived_and_restore_continues(self) -> None:
        dll = self.game / "plugin.dll"
        dll.write_bytes(b"old plugin")
        self.tx.put(dll, b"installed plugin")
        self.mri.write_bytes(b"previous import")
        self.tx.put(self.mri, b"offline import")
        self.mri.write_bytes(b"runtime regenerated import")
        stamp = self.mri.stat().st_mtime_ns
        with contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(installer.Transaction.restore(self.tx.folder, self.game), 2)
        self.assertIn("RESTORE_CACHE_SAVED", output.getvalue())
        self.assertEqual(self.mri.read_bytes(), b"previous import")
        self.assertEqual(dll.read_bytes(), b"old plugin")
        state = self.state()
        self.assertEqual(state["status"], "restored")
        self.assertEqual(state["entries"][-1]["restoreDisposition"], "derived-cache-restore")
        self.assertEqual((self.tx.folder / state["entries"][-1]["backup"]).read_bytes(), b"previous import")
        saved = self.tx.folder / state["entries"][-1]["cacheConflictBackup"]
        self.assertEqual(saved.read_bytes(), b"runtime regenerated import")
        self.assertEqual(saved.stat().st_mtime_ns, stamp)
        self.assertEqual(installer.Transaction.restore(self.tx.folder, self.game), 0)

    def test_removed_existing_morph_import_is_restored(self) -> None:
        self.mri.write_bytes(b"previous import")
        self.tx.put(self.mri, b"offline import")
        self.mri.unlink()
        self.assertEqual(installer.Transaction.restore(self.tx.folder, self.game), 1)
        self.assertEqual(self.mri.read_bytes(), b"previous import")
        self.assertEqual(self.state()["status"], "restored")

    def test_unchanged_morph_import_restores_original_bytes(self) -> None:
        self.mri.write_bytes(b"previous import")
        self.tx.put(self.mri, b"offline import")
        self.assertEqual(installer.Transaction.restore(self.tx.folder, self.game), 1)
        self.assertEqual(self.mri.read_bytes(), b"previous import")

    def test_new_unchanged_morph_import_is_removed(self) -> None:
        self.tx.put(self.mri, b"offline import")
        self.assertEqual(installer.Transaction.restore(self.tx.folder, self.game), 1)
        self.assertFalse(self.mri.exists())

    def test_later_dll_config_and_var_edits_still_raise_conflicts(self) -> None:
        for suffix in (".dll", ".cfg", ".var"):
            with self.subTest(suffix=suffix):
                path = self.game / ("user" + suffix)
                path.write_bytes(b"original")
                tx = installer.Transaction(self.game, self.cache, self.game / ("backup" + suffix))
                tx.put(path, b"installed")
                path.write_bytes(b"later user edit")
                with self.assertRaisesRegex(RuntimeError, "RESTORE_CONFLICT"):
                    installer.Transaction.restore(tx.folder, self.game)
                self.assertEqual(path.read_bytes(), b"later user edit")
                self.assertEqual(installer.json_read(tx.state_path)["status"], "installing")

    def test_morph_exception_does_not_match_other_files(self) -> None:
        paths = [self.game / self.mri.name,
                 self.cache / "VaMMemory/Other" / self.mri.name,
                 self.mri.parent / "user.mri",
                 self.mri.parent / ("imp-" + "AB" * 31 + ".mri"),
                 self.mri.parent / "nested" / self.mri.name]
        for index, path in enumerate(paths):
            with self.subTest(path=path):
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"original")
                tx = installer.Transaction(self.game, self.cache, self.game / ("negative" + str(index)))
                tx.put(path, b"installed")
                path.write_bytes(b"later edit")
                with self.assertRaisesRegex(RuntimeError, "RESTORE_CONFLICT"):
                    installer.Transaction.restore(tx.folder, self.game)
                self.assertEqual(path.read_bytes(), b"later edit")

    def test_runtime_symbolic_link_is_not_treated_as_generated_cache(self) -> None:
        self.mri.write_bytes(b"original")
        self.tx.put(self.mri, b"installed")
        target = self.game / "shared source"
        target.write_bytes(b"user data")
        self.mri.unlink()
        try:
            self.mri.symlink_to(target)
        except OSError as error:
            self.skipTest("Symlink creation unavailable: " + str(error))
        with self.assertRaisesRegex(RuntimeError, "RESTORE_CONFLICT"):
            installer.Transaction.restore(self.tx.folder, self.game)
        self.assertTrue(self.mri.is_symlink())
        self.assertEqual(target.read_bytes(), b"user data")

    def test_empty_interrupted_transaction_is_finalized(self) -> None:
        self.assertEqual(self.state()["entries"], [])
        with patch.object(sys, "argv", ["installer.py", "restore", "--root", str(self.game)]), contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(installer.main(), 0)
        self.assertIn("RESTORE_PASS files=0", output.getvalue())
        self.assertEqual(self.state()["status"], "restored")

    def test_other_generated_cache_formats_are_archived(self) -> None:
        paths = [self.cache / "VaMMemory/ClassificationReceipts" / ("AB" * 32 + ".mcr"),
                 self.cache / "VaMMemory/PackageTrees" / ("AB" * 32 + ".vjt"),
                 self.cache / "PackageJSON/作者.预设包.1.vamcachejson",
                 self.cache / "PackageJSON/作者.预设包.1.vamcachemeta",
                 self.cache / "Textures 中文/face_C.vamcache",
                 self.cache / "Textures 中文/face_C.vamcachemeta"]
        for index, path in enumerate(paths):
            with self.subTest(path=path):
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"original")
                tx = installer.Transaction(self.game, self.cache, self.game / ("cache" + str(index)))
                tx.put(path, b"installed")
                path.write_bytes(b"runtime regenerated")
                self.assertEqual(installer.Transaction.restore(tx.folder, self.game), 1)
                self.assertEqual(path.read_bytes(), b"original")
                row = installer.json_read(tx.state_path)["entries"][0]
                self.assertEqual((tx.folder / row["cacheConflictBackup"]).read_bytes(), b"runtime regenerated")

    def test_cache_pairs_and_partial_old_restore_end_consistent(self) -> None:
        for family in ("PackageJSON", "BC7"):
            for partial in (False, True):
                with self.subTest(family=family, partial=partial):
                    parent = self.cache / ("PackageJSON" if family == "PackageJSON" else "textures")
                    parent.mkdir(exist_ok=True)
                    data = parent / ("Creator.Package.1.vamcachejson" if family == "PackageJSON" else "skin_C.vamcache")
                    meta = parent / ("Creator.Package.1.vamcachemeta" if family == "PackageJSON" else "skin_C.vamcachemeta")
                    data.write_bytes(b"original data")
                    meta.write_bytes(b"original meta")
                    tx = installer.Transaction(self.game, self.cache, self.game / (family + str(partial)))
                    tx.put_pair([(data, b"installed data"), (meta, b"installed meta")])
                    data.write_bytes(b"runtime data")
                    meta.write_bytes(b"runtime meta")
                    if partial:
                        meta.write_bytes(b"original meta")
                        tx.state["entries"][1]["status"] = "restored"
                        tx.save()
                    self.assertEqual(installer.Transaction.restore(tx.folder, self.game), 1 if partial else 2)
                    self.assertEqual((data.read_bytes(), meta.read_bytes()), (b"original data", b"original meta"))
                    self.assertEqual(installer.Transaction.restore(tx.folder, self.game), 0)
                    rows = installer.json_read(tx.state_path)["entries"]
                    self.assertEqual((tx.folder / rows[0]["cacheConflictBackup"]).read_bytes(), b"runtime data")

    def test_preservation_failure_does_not_overwrite_runtime_cache(self) -> None:
        self.mri.write_bytes(b"original")
        self.tx.put(self.mri, b"installed")
        self.mri.write_bytes(b"runtime")
        def partial_copy(source: Path, destination: Path) -> None:
            Path(destination).write_bytes(b"partial")
            raise PermissionError("backup denied")

        with patch.object(installer.shutil, "copy2", side_effect=partial_copy):
            with self.assertRaises(PermissionError):
                installer.Transaction.restore(self.tx.folder, self.game)
        self.assertEqual(self.mri.read_bytes(), b"runtime")
        self.assertEqual(self.state()["status"], "installing")
        self.assertEqual(list((self.tx.folder / "preserved-cache").iterdir()), [])

    def test_interruption_after_cache_preservation_is_resumable(self) -> None:
        self.mri.write_bytes(b"original")
        self.tx.put(self.mri, b"installed")
        self.mri.write_bytes(b"runtime")
        original_atomic = installer.atomic

        def interrupted(path: Path, data: bytes) -> None:
            if path == self.mri:
                raise OSError("interrupted before cache restore")
            original_atomic(path, data)

        with patch.object(installer, "atomic", side_effect=interrupted):
            with self.assertRaisesRegex(OSError, "interrupted before cache restore"):
                installer.Transaction.restore(self.tx.folder, self.game)
        self.assertEqual(self.mri.read_bytes(), b"runtime")
        self.assertEqual(installer.Transaction.restore(self.tx.folder, self.game), 1)
        self.assertEqual(self.mri.read_bytes(), b"original")
        saved = list((self.tx.folder / "preserved-cache").iterdir())
        self.assertEqual(len(saved), 1)
        self.assertEqual(saved[0].read_bytes(), b"runtime")


if __name__ == "__main__":
    unittest.main(verbosity=2)
