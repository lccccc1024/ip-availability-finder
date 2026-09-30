import os
import sys
from pathlib import Path
import tempfile
import time
import unittest
from ipaddress import IPv4Address

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ping_candidate_finder.app import Application
from ping_candidate_finder.scan import ScanProgress


@unittest.skipUnless(sys.platform == "win32", "Windows GUI test")
class LayoutTests(unittest.TestCase):
    def test_results_visible_normally_and_page_scrolls_when_small(self):
        with tempfile.TemporaryDirectory() as settings_dir:
            previous = os.environ.get("LOCALAPPDATA")
            os.environ["LOCALAPPDATA"] = settings_dir
            app = None
            try:
                app = Application()
                app.attributes("-alpha", 0.0)
                app.geometry("900x790")
                app.update()
                time.sleep(0.04)
                app.update()
                self.assertFalse(app.content_scrollbar.winfo_manager())
                table_bottom = app.table.winfo_rooty() - app.winfo_rooty() + app.table.winfo_height()
                self.assertLess(table_bottom, app.winfo_height())

                app.geometry("700x500")
                app.update()
                self.assertEqual(app._layout_mode, "compact")
                self.assertEqual(app.content_scrollbar.winfo_manager(), "pack")
                app.content_canvas.event_generate("<MouseWheel>", delta=-120)
                app.update()
                self.assertGreater(app.content_canvas.yview()[0], 0)

                app.content_canvas.yview_moveto(0)
                app._show_progress(ScanProgress(1, 0, 0, (IPv4Address("192.168.1.10"),), 254))
                app.update()
                self.assertGreater(app.content_canvas.yview()[0], 0)
                self.assertEqual(len(app.table.get_children()), 1)
                self.assertTrue(app.copy_all_button._enabled)
                app._copy_all()
                self.assertEqual(app.clipboard_get(), "192.168.1.10")

                app.content_canvas.yview_moveto(0)
                app.table.event_generate("<MouseWheel>", delta=-120)
                app.update()
                self.assertGreater(app.content_canvas.yview()[0], 0)
            finally:
                if app is not None:
                    app.destroy()
                if previous is None:
                    os.environ.pop("LOCALAPPDATA", None)
                else:
                    os.environ["LOCALAPPDATA"] = previous
