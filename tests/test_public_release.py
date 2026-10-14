"""Publication guard regressions use constructed synthetic data only."""
import importlib.util
import unittest
from pathlib import Path


spec = importlib.util.spec_from_file_location("guard", Path(__file__).parents[1] / "tools/check_public_release.py")
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


class PublicationGuardTests(unittest.TestCase):
    def test_blocks_binary_and_research(self):
        self.assertIn("unexpected-file", guard.inspect("research/notes.md", b"notes"))
        self.assertIn("unexpected-file", guard.inspect("src/game.dll", b"MZ"))
        self.assertIn("binary-content", guard.inspect("src/test.cs", b"x\x00y"))

    def test_blocks_paths_addresses_and_context(self):
        path = "/mnt/" + "x/" + "private/file"
        self.assertIn("local-path", guard.inspect("README.md", path.encode()))
        windows_path = "Z:" + chr(92) + "GameClient" + chr(92) + "private"
        self.assertIn("local-path", guard.inspect("src/test.cs", windows_path.encode()))
        address = ".".join(map(str, [192, 168, 2, 17]))
        self.assertIn("local-network-address", guard.inspect("docs/test.md", address.encode()))
        context = "\u672c\u8f6e" + " test notes"
        self.assertIn("conversation-prose", guard.inspect("README.md", context.encode()))

    def test_blocks_generated_secret_shape(self):
        token = "gh" + "p_" + "Z" * 40
        self.assertIn("token", guard.inspect("src/test.cs", token.encode()))

    def test_accepts_product_text_and_game_version(self):
        self.assertEqual([], guard.inspect("README.md", "EFT 0.16.9.40743; GPL-3.0-only; 队友交流".encode()))
        self.assertEqual([], guard.inspect("src/Plugin.cs", b"namespace ImmersiveVoice {}"))


if __name__ == "__main__":
    unittest.main()
