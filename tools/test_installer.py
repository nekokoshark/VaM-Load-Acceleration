"""Pure file transaction and catalog tests (no VaM/Unity process)."""
from __future__ import annotations
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('installer', ROOT / 'package/VaMAcceleration/tools/installer.py')
assert spec and spec.loader
installer = importlib.util.module_from_spec(spec); spec.loader.exec_module(installer)

class InstallerTests(unittest.TestCase):
    def test_default_relative_absolute_cache(self) -> None:
        with tempfile.TemporaryDirectory() as d:
            game = Path(d)
            self.assertEqual(installer.cache_root(game), game / 'Cache')
            (game / 'prefs.json').write_text('{"cacheFolder":"cache 中文 space"}', 'utf-8')
            self.assertEqual(installer.cache_root(game), game / 'cache 中文 space')
            absolute = game.parent / 'fixture external cache'
            (game / 'prefs.json').write_text(json.dumps(dict(cacheFolder=str(absolute))), 'utf-8')
            self.assertEqual(installer.cache_root(game), absolute)

    def test_pair_restores_bytes_and_timestamps(self) -> None:
        with tempfile.TemporaryDirectory() as d, patch.object(installer, 'game_stopped'):
            game = Path(d); cache = game / 'cache'; cache.mkdir()
            a, b = cache / 'data', cache / 'meta'; a.write_bytes(b'old data'); b.write_bytes(b'old meta')
            stamps = [a.stat().st_mtime_ns, b.stat().st_mtime_ns]
            tx = installer.Transaction(game, cache, game / 'backup')
            tx.put_pair([(a, b'new data'), (b, b'new meta')])
            self.assertEqual(a.read_bytes(), b'new data')
            self.assertEqual(installer.Transaction.restore(tx.folder, game), 2)
            self.assertEqual((a.read_bytes(), b.read_bytes()), (b'old data', b'old meta'))
            self.assertEqual([a.stat().st_mtime_ns, b.stat().st_mtime_ns], stamps)

    def test_later_user_edit_is_not_overwritten(self) -> None:
        with tempfile.TemporaryDirectory() as d, patch.object(installer, 'game_stopped'):
            game = Path(d); p = game / 'file'; p.write_bytes(b'original')
            tx = installer.Transaction(game, game / 'cache', game / 'backup'); tx.put(p, b'patched')
            p.write_bytes(b'later user edit')
            with self.assertRaisesRegex(RuntimeError, 'RESTORE_CONFLICT'): installer.Transaction.restore(tx.folder, game)
            self.assertEqual(p.read_bytes(), b'later user edit')

    def test_running_game_prevents_write(self) -> None:
        with tempfile.TemporaryDirectory() as d:
            game = Path(d); p = game / 'file'; p.write_bytes(b'original')
            tx = installer.Transaction(game, game / 'cache', game / 'backup')
            with patch.object(installer, 'game_stopped', side_effect=RuntimeError('GAME_RUNNING')):
                with self.assertRaisesRegex(RuntimeError, 'GAME_RUNNING'): tx.put(p, b'patched')
            self.assertEqual(p.read_bytes(), b'original')

    def test_link_restore_reuses_original_link(self) -> None:
        with tempfile.TemporaryDirectory() as d, patch.object(installer, 'game_stopped'):
            game = Path(d); source = game / 'original'; source.write_bytes(b'shared source')
            p = game / 'linked.var'
            try: p.symlink_to('original')
            except OSError as e: self.skipTest('Symlink creation is not available in this test account: ' + str(e))
            tx = installer.Transaction(game, game / 'cache', game / 'backup'); tx.put(p, b'patched')
            self.assertFalse(p.is_symlink()); self.assertEqual(source.read_bytes(), b'shared source')
            with patch.object(Path, 'symlink_to', side_effect=PermissionError('No creation privilege')):
                self.assertEqual(installer.Transaction.restore(tx.folder, game), 1)
            self.assertTrue(p.is_symlink()); self.assertEqual(p.read_bytes(), b'shared source')

    def test_duplicate_uids_are_not_guessed(self) -> None:
        with tempfile.TemporaryDirectory() as d:
            game = Path(d); (game / 'nested').mkdir()
            (game / 'A.B.1.var').touch(); (game / 'nested/A.B.1.var').touch(); (game / 'C.D.1.var').touch()
            self.assertEqual(installer.catalog(game), [game / 'C.D.1.var'])

    def test_owned_configuration_only(self) -> None:
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / 'config'; p.write_text('[Other]\nToken = local-value\n[TextureLoading]\nBc7ToolPath = old\n')
            result = installer.configuration(p, {('TextureLoading', 'Bc7ToolPath'): 'new'}).decode()
            self.assertIn('Token = local-value', result); self.assertIn('Bc7ToolPath = new', result)
            self.assertNotIn('Bc7ToolPath = old', result)

if __name__ == '__main__': unittest.main(verbosity=2)
