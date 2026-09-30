from __future__ import annotations

import json
import os
from pathlib import Path
from queue import Empty, Queue
from threading import Event, Thread
import time
import tkinter as tk
from tkinter import messagebox, ttk

from .icmp import WindowsIcmpProber
from .material import (
    BACKGROUND, FONT, MUTED, ON_PRIMARY_CONTAINER, OUTLINE, PRIMARY,
    PRIMARY_CONTAINER, SURFACE, SURFACE_SOFT, TEXT, WARNING_CONTAINER,
    WARNING_TEXT, MaterialField, PillButton, RoundedCard, rounded_rectangle,
)
from .scan import ProbeResult, ScanOptions, ScanProgress, parse_options, scan


APP_NAME = "Ping 候选 IP 查找"


class Application(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title(APP_NAME)
        width = min(960, max(640, self.winfo_screenwidth() - 100))
        height = min(820, max(480, self.winfo_screenheight() - 120))
        self.geometry(f"{width}x{height}")
        self.minsize(640, 460)
        self.configure(bg=BACKGROUND)
        self._events: Queue[tuple[str, object]] = Queue()
        self._cancel = Event()
        self._running = False
        self._started_at = 0.0
        self._target_count = 0
        self._gateway_warning = False
        self._entries: list[tk.Entry] = []
        self._layout_mode = ""
        self._settings_path = Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "PingCandidateFinder" / "settings.json"
        self._build_styles()
        self._build_ui()
        self._load_settings()
        self.after(100, self._drain_events)
        self.protocol("WM_DELETE_WINDOW", self._close)

    def _build_styles(self) -> None:
        style = ttk.Style(self)
        style.theme_use("clam")
        style.configure("M3.Treeview", background=SURFACE, fieldbackground=SURFACE, foreground=TEXT,
                        borderwidth=0, relief="flat", rowheight=42, font=(FONT, 10))
        style.map("M3.Treeview", background=[("selected", PRIMARY_CONTAINER)],
                  foreground=[("selected", ON_PRIMARY_CONTAINER)])
        style.configure("M3.Treeview.Heading", background=SURFACE_SOFT, foreground=MUTED,
                        borderwidth=0, relief="flat", font=(FONT, 9, "bold"), padding=(10, 8))
        style.map("M3.Treeview.Heading", background=[("active", SURFACE_SOFT)])
        style.configure("M3.Horizontal.TProgressbar", troughcolor=SURFACE_SOFT,
                        background=PRIMARY, bordercolor=SURFACE_SOFT, lightcolor=PRIMARY,
                        darkcolor=PRIMARY, thickness=7)
        style.configure("M3.Vertical.TScrollbar", background=OUTLINE, troughcolor=BACKGROUND,
                        bordercolor=BACKGROUND, arrowcolor=MUTED)

    def _build_ui(self) -> None:
        header = tk.Frame(self, bg=BACKGROUND, height=78)
        header.pack(fill="x")
        header.pack_propagate(False)
        logo = tk.Canvas(header, width=44, height=44, bg=BACKGROUND, highlightthickness=0)
        logo.pack(side="left", padx=(24, 12), pady=15)
        rounded_rectangle(logo, 0, 0, 43, 43, 15, PRIMARY)
        logo.create_text(22, 22, text="IP", fill=SURFACE, font=("Segoe UI", 15, "bold"))
        title = tk.Frame(header, bg=BACKGROUND)
        title.pack(side="left", pady=12)
        tk.Label(title, text="Ping 地址查找", bg=BACKGROUND, fg=TEXT, font=(FONT, 17, "bold"), anchor="w").pack(fill="x")
        tk.Label(title, text="快速找出待确认的 IPv4 地址", bg=BACKGROUND, fg=MUTED, font=(FONT, 9), anchor="w").pack(fill="x")
        tk.Label(header, text="本地扫描  ·  IPv4", bg=PRIMARY_CONTAINER, fg=ON_PRIMARY_CONTAINER,
                 padx=13, pady=7, font=(FONT, 9, "bold")).pack(side="right", padx=24)

        viewport = tk.Frame(self, bg=BACKGROUND)
        viewport.pack(fill="both", expand=True)
        self.content_canvas = tk.Canvas(viewport, bg=BACKGROUND, highlightthickness=0, borderwidth=0)
        self.content_scrollbar = ttk.Scrollbar(viewport, orient="vertical", command=self.content_canvas.yview,
                                               style="M3.Vertical.TScrollbar")
        self.content_canvas.configure(yscrollcommand=self.content_scrollbar.set)
        self.content_canvas.pack(side="left", fill="both", expand=True)
        body = tk.Frame(self.content_canvas, bg=BACKGROUND, padx=20, pady=8)
        self.content_body = body
        self.content_window = self.content_canvas.create_window((0, 0), window=body, anchor="nw")
        self.content_canvas.bind("<Configure>", self._update_content_scroll)
        body.bind("<Configure>", self._update_content_scroll)
        self.bind_all("<MouseWheel>", self._on_mouse_wheel, add="+")

        hero = tk.Frame(body, bg=BACKGROUND)
        hero.pack(fill="x", pady=(0, 13))
        tk.Label(hero, text="找到下一批待确认 IP", bg=BACKGROUND, fg=TEXT,
                 font=(FONT, 18, "bold"), anchor="w").pack(fill="x")
        tk.Label(hero, text="设好范围和数量，剩下的交给扫描。", bg=BACKGROUND, fg=MUTED,
                 font=(FONT, 9), anchor="w").pack(fill="x", pady=(2, 0))

        self.network_var = tk.StringVar()
        self.mask_var = tk.StringVar(value="255.255.255.0")
        self.gateway_var = tk.StringVar()
        self.start_var = tk.StringVar()
        self.count_var = tk.StringVar(value="5")

        self.main_area = tk.Frame(body, bg=BACKGROUND)
        self.main_area.pack(fill="x")
        self.settings_card = RoundedCard(self.main_area, padding=18)
        form = self.settings_card.content
        tk.Label(form, text="扫描设置", bg=SURFACE, fg=TEXT, font=(FONT, 13, "bold"), anchor="w").pack(fill="x")
        tk.Label(form, text="需要的地址数量可以一次填好", bg=SURFACE, fg=MUTED,
                 font=(FONT, 9), anchor="w").pack(fill="x", pady=(2, 13))
        fields = tk.Frame(form, bg=SURFACE)
        fields.pack(fill="x")
        for column in (0, 1):
            fields.grid_columnconfigure(column, weight=1, uniform="fields")
        self._field(fields, 0, 0, "网段 IP", self.network_var, "例如 192.168.10.0")
        self._field(fields, 0, 1, "子网掩码", self.mask_var, "例如 23 或 255.255.254.0")
        self._field(fields, 1, 0, "候选数量", self.count_var, "例如 5")
        self._field(fields, 1, 1, "网关 · 可选", self.gateway_var, "不通时提醒，继续扫描")
        self._field(fields, 2, 0, "起始 IP · 可选", self.start_var, "留空则从网段起点开始")
        tk.Label(fields, text="找到指定数量后\n自动停止扫描", bg=PRIMARY_CONTAINER,
                 fg=ON_PRIMARY_CONTAINER, font=(FONT, 9, "bold"), justify="left",
                 padx=13, pady=10, anchor="w").grid(row=2, column=1, sticky="ew", padx=(5, 0), pady=(0, 6))
        actions = tk.Frame(form, bg=SURFACE)
        actions.pack(fill="x", pady=(9, 0))
        self.start_button = PillButton(actions, text="开始查找", command=self._start,
                                       width=136, variant="filled")
        self.start_button.pack(side="left")
        self.stop_button = PillButton(actions, text="停止", command=self._stop,
                                      width=82, enabled=False)
        self.stop_button.pack(side="left", padx=8)
        tk.Label(actions, text="最大 /16", bg=SURFACE, fg=MUTED, font=(FONT, 9)).pack(side="right")

        self.result_card = RoundedCard(self.main_area, padding=18)
        result = self.result_card.content
        result_heading = tk.Frame(result, bg=SURFACE)
        result_heading.pack(fill="x")
        tk.Label(result_heading, text="候选地址", bg=SURFACE, fg=TEXT,
                 font=(FONT, 13, "bold")).pack(side="left")
        self.result_count_label = tk.Label(result_heading, text="0 个", bg=PRIMARY_CONTAINER,
                                            fg=ON_PRIMARY_CONTAINER, padx=10, pady=5,
                                            font=(FONT, 9, "bold"))
        self.result_count_label.pack(side="right")
        tk.Label(result, text="连续两次未回应 ping 的 IP", bg=SURFACE, fg=MUTED,
                 font=(FONT, 9), anchor="w").pack(fill="x", pady=(3, 11))
        self.progressbar = ttk.Progressbar(result, mode="determinate", style="M3.Horizontal.TProgressbar")
        self.progressbar.pack(fill="x", pady=(0, 12))

        table_frame = tk.Frame(result, bg=SURFACE)
        table_frame.pack(fill="both", expand=True)
        self.table = ttk.Treeview(table_frame, columns=("index", "ip", "basis"),
                                  show="headings", selectmode="browse", height=5, style="M3.Treeview")
        self.table.heading("index", text="序号")
        self.table.heading("ip", text="IP 地址")
        self.table.heading("basis", text="判断依据")
        self.table.column("index", width=53, anchor="center", stretch=False)
        self.table.column("ip", width=145, anchor="w")
        self.table.column("basis", width=164, anchor="w")
        scrollbar = ttk.Scrollbar(table_frame, orient="vertical", command=self.table.yview,
                                  style="M3.Vertical.TScrollbar")
        self.table.configure(yscrollcommand=scrollbar.set)
        self.table.pack(side="left", fill="both", expand=True)
        scrollbar.pack(side="right", fill="y")
        self.empty_label = tk.Label(table_frame, text="还没有候选地址\n开始扫描后会显示在这里", bg=SURFACE,
                                     fg=MUTED, font=(FONT, 10), justify="center")
        self.empty_label.place(relx=0.5, rely=0.55, anchor="center")
        self.table.bind("<<TreeviewSelect>>", lambda _: self.copy_button.set_enabled(bool(self.table.selection())))
        self.table.bind("<Double-1>", lambda _: self._copy_selected())
        self.table.bind("<MouseWheel>", self._on_table_wheel)
        copy_actions = tk.Frame(result, bg=SURFACE)
        copy_actions.pack(fill="x", pady=(12, 8))
        self.copy_button = PillButton(copy_actions, text="复制选中", command=self._copy_selected,
                                      width=110, enabled=False)
        self.copy_button.pack(side="left")
        self.copy_all_button = PillButton(copy_actions, text="复制全部", command=self._copy_all,
                                          width=110, variant="text", enabled=False)
        self.copy_all_button.pack(side="left", padx=7)
        tk.Label(result, text="未回应只代表待确认，请按你的网络规则分配。",
                 bg=WARNING_CONTAINER, fg=WARNING_TEXT, padx=10, pady=8,
                 font=(FONT, 9), anchor="w").pack(fill="x")

        metrics = tk.Frame(body, bg=BACKGROUND)
        metrics.pack(fill="x", pady=(12, 0))
        for column in range(3):
            metrics.grid_columnconfigure(column, weight=1, uniform="metric")
        self.checked_label = self._stat(metrics, 0, "已检查", "0", SURFACE_SOFT)
        self.replied_label = self._stat(metrics, 1, "已回应", "0", SURFACE_SOFT)
        self.elapsed_label = self._stat(metrics, 2, "耗时", "0.0 秒", PRIMARY_CONTAINER)

        self.status_var = tk.StringVar(value="填写网段和候选数量后开始查找。")
        tk.Label(body, textvariable=self.status_var, bg=BACKGROUND, fg=MUTED,
                 anchor="w", font=(FONT, 9)).pack(fill="x", pady=(10, 0))

    def _layout_panels(self, width: int) -> bool:
        mode = "wide" if width >= 820 else "compact"
        if mode == self._layout_mode:
            return False
        self._layout_mode = mode
        self.settings_card.grid_forget()
        self.result_card.grid_forget()
        if mode == "wide":
            self.main_area.grid_columnconfigure(0, weight=5)
            self.main_area.grid_columnconfigure(1, weight=4)
            self.settings_card.grid(row=0, column=0, sticky="nsew", padx=(0, 7))
            self.result_card.grid(row=0, column=1, sticky="nsew", padx=(7, 0))
        else:
            self.main_area.grid_columnconfigure(0, weight=1)
            self.main_area.grid_columnconfigure(1, weight=0)
            self.settings_card.grid(row=0, column=0, sticky="ew")
            self.result_card.grid(row=1, column=0, sticky="ew", pady=(12, 0))
        return True

    def _update_content_scroll(self, _event: tk.Event | None = None) -> None:
        canvas = self.content_canvas
        if not canvas.winfo_exists():
            return
        width = max(1, canvas.winfo_width())
        if self._layout_panels(width):
            self.after(20, self._update_content_scroll)
        height = max(canvas.winfo_height(), self.content_body.winfo_reqheight())
        canvas.itemconfigure(self.content_window, width=width, height=height)
        canvas.configure(scrollregion=(0, 0, width, height))
        if self.content_body.winfo_reqheight() > canvas.winfo_height() + 1:
            if not self.content_scrollbar.winfo_manager():
                self.content_scrollbar.pack(side="right", fill="y")
        elif self.content_scrollbar.winfo_manager():
            self.content_scrollbar.pack_forget()
            canvas.yview_moveto(0)

    def _on_mouse_wheel(self, event: tk.Event) -> None:
        if not self.content_scrollbar.winfo_manager():
            return
        self.content_canvas.yview_scroll(-1 if event.delta > 0 else 1, "units")

    def _on_table_wheel(self, event: tk.Event) -> str:
        direction = -1 if event.delta > 0 else 1
        first, last = self.table.yview()
        if (direction < 0 and first > 0) or (direction > 0 and last < 1):
            self.table.yview_scroll(direction, "units")
        elif self.content_scrollbar.winfo_manager():
            self.content_canvas.yview_scroll(direction, "units")
        return "break"

    def _show_results_if_needed(self) -> None:
        canvas = self.content_canvas
        if not self.content_scrollbar.winfo_manager():
            return
        visible_bottom = canvas.canvasy(canvas.winfo_height())
        result_top = self.result_card.winfo_rooty() - self.content_body.winfo_rooty()
        result_bottom = result_top + min(self.result_card.winfo_height(), 150)
        if result_bottom > visible_bottom:
            scrollable = max(1, self.content_body.winfo_height() - canvas.winfo_height())
            canvas.yview_moveto(min(1.0, max(0.0, (result_top - 12) / scrollable)))

    def _field(self, parent: tk.Frame, row: int, column: int, label: str, variable: tk.StringVar, hint: str) -> None:
        field = MaterialField(parent, label=label, variable=variable, hint=hint)
        field.grid(row=row, column=column, sticky="ew", padx=(0, 5) if column == 0 else (5, 0), pady=(0, 6))
        self._entries.append(field.entry)
        if row == 0 and column == 0:
            field.entry.focus_set()

    def _stat(self, parent: tk.Frame, column: int, title: str, value: str, color: str) -> tk.Label:
        card = RoundedCard(parent, fill=color, padding=12, radius=19)
        card.grid(row=0, column=column, sticky="ew", padx=(0 if column == 0 else 5, 0 if column == 2 else 5))
        tk.Label(card.content, text=title, bg=color, fg=MUTED, font=(FONT, 9), anchor="w").pack(fill="x")
        label = tk.Label(card.content, text=value, bg=color, fg=TEXT,
                         font=("Segoe UI", 17, "bold"), anchor="w")
        label.pack(fill="x")
        return label

    def _load_settings(self) -> None:
        try:
            data = json.loads(self._settings_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return
        for name, variable in self._variables().items():
            if isinstance(data.get(name), str):
                variable.set(data[name])

    def _save_settings(self) -> None:
        try:
            self._settings_path.parent.mkdir(parents=True, exist_ok=True)
            self._settings_path.write_text(json.dumps({name: var.get() for name, var in self._variables().items()}, ensure_ascii=False, indent=2), encoding="utf-8")
        except OSError:
            pass

    def _variables(self) -> dict[str, tk.StringVar]:
        return {"network": self.network_var, "mask": self.mask_var, "gateway": self.gateway_var, "start": self.start_var, "count": self.count_var}

    def _start(self) -> None:
        try:
            options = parse_options(self.network_var.get(), self.mask_var.get(), self.gateway_var.get(), self.start_var.get(), self.count_var.get())
        except ValueError as exc:
            messagebox.showerror("输入有误", str(exc), parent=self)
            return
        self._save_settings()
        self._cancel = Event()
        self._running = True
        self._target_count = options.count
        self._gateway_warning = False
        self._started_at = time.monotonic()
        self.content_canvas.yview_moveto(0)
        for item in self.table.get_children():
            self.table.delete(item)
        self.empty_label.configure(text="正在查找候选地址…")
        self.empty_label.place(relx=0.5, rely=0.55, anchor="center")
        self.result_count_label.configure(text="0 个")
        self.checked_label.configure(text="0")
        self.replied_label.configure(text="0")
        self.elapsed_label.configure(text="0.0 秒")
        self.progressbar.configure(maximum=options.network.num_addresses - 2, value=0)
        self.copy_all_button.set_enabled(False)
        self.copy_button.set_enabled(False)
        self.start_button.set_enabled(False)
        self.stop_button.set_enabled(True)
        for entry in self._entries:
            entry.configure(state="disabled")
        self.status_var.set("正在检查网关…" if options.gateway else "正在查找候选地址…")
        Thread(target=self._worker, args=(options, self._cancel), daemon=True).start()

    def _worker(self, options: ScanOptions, cancelled: Event) -> None:
        try:
            prober = WindowsIcmpProber()
            if options.gateway is not None:
                gateway_result = ProbeResult.UNKNOWN
                for _ in range(2):
                    if cancelled.is_set():
                        break
                    gateway_result = prober.probe(options.gateway, options.timeout_ms)
                    if gateway_result is ProbeResult.REPLIED:
                        break
                if gateway_result is not ProbeResult.REPLIED and not cancelled.is_set():
                    self._events.put(("gateway_warning", str(options.gateway)))
            if cancelled.is_set():
                self._events.put(("done", ScanProgress(0, 0, 0, (), 0)))
                return
            last_report = 0.0
            last_candidates = 0

            def report(progress: ScanProgress) -> None:
                nonlocal last_report, last_candidates
                now = time.monotonic()
                if len(progress.candidates) != last_candidates or now - last_report >= 0.2:
                    self._events.put(("progress", progress))
                    last_report = now
                    last_candidates = len(progress.candidates)

            result = scan(options, prober, cancelled, report)
            self._events.put(("done", result))
        except Exception as exc:
            self._events.put(("error", str(exc)))

    def _drain_events(self) -> None:
        try:
            while True:
                kind, value = self._events.get_nowait()
                if kind == "progress":
                    self._show_progress(value)
                elif kind == "gateway_warning":
                    self._gateway_warning = True
                    self.status_var.set(f"网关 {value} 未回应 ping；继续扫描。请谨慎核对候选地址。")
                elif kind == "done":
                    self._finish(value)
                elif kind == "error":
                    self._running = False
                    self.start_button.set_enabled(True)
                    self.stop_button.set_enabled(False)
                    for entry in self._entries:
                        entry.configure(state="normal")
                    self.status_var.set("扫描发生错误。")
                    messagebox.showerror("扫描错误", str(value), parent=self)
        except Empty:
            pass
        if self.winfo_exists():
            if self._running:
                self.elapsed_label.configure(text=f"{time.monotonic() - self._started_at:.1f} 秒")
            self.after(100, self._drain_events)

    def _show_progress(self, progress: ScanProgress) -> None:
        self.checked_label.configure(text=str(progress.checked))
        self.replied_label.configure(text=str(progress.replied))
        self.progressbar.configure(maximum=max(1, progress.total), value=progress.checked)
        displayed = len(self.table.get_children())
        for index, address in enumerate(progress.candidates[displayed:], start=displayed + 1):
            self.table.insert("", "end", values=(index, str(address), "连续 2 次未回应 ping"))
        self.result_count_label.configure(text=f"{len(progress.candidates)} 个")
        self.copy_all_button.set_enabled(bool(progress.candidates))
        if progress.candidates:
            self.empty_label.place_forget()
        if displayed == 0 and progress.candidates:
            self._show_results_if_needed()

    def _finish(self, result: ScanProgress) -> None:
        self._show_progress(result)
        self._running = False
        self.elapsed_label.configure(text=f"{time.monotonic() - self._started_at:.1f} 秒")
        self.start_button.set_enabled(True)
        self.stop_button.set_enabled(False)
        if not result.candidates:
            self.empty_label.configure(text="没有找到候选地址")
        for entry in self._entries:
            entry.configure(state="normal")
        if self._cancel.is_set():
            message = f"已停止，找到 {len(result.candidates)} 个待确认候选。"
        elif len(result.candidates) >= self._target_count:
            message = f"已找到 {len(result.candidates)} 个待确认候选，自动停止。"
        else:
            message = f"已查完整个网段，找到 {len(result.candidates)} 个待确认候选。"
        if result.unknown:
            message += f" {result.unknown} 个地址因网络错误无法判断。"
        if self._gateway_warning:
            message += " 网关未回应 ping，请额外核对。"
        self.status_var.set(message)

    def _stop(self) -> None:
        self._cancel.set()
        self.stop_button.set_enabled(False)
        self.status_var.set("正在停止…")

    def _copy_selected(self) -> None:
        selection = self.table.selection()
        if selection:
            self._copy(self.table.item(selection[0], "values")[1])

    def _copy_all(self) -> None:
        values = [self.table.item(item, "values")[1] for item in self.table.get_children()]
        if values:
            self._copy("\n".join(values))

    def _copy(self, value: str) -> None:
        self.clipboard_clear()
        self.clipboard_append(value)
        self.update_idletasks()
        self.status_var.set("已复制到剪贴板。")

    def _close(self) -> None:
        self._cancel.set()
        self._save_settings()
        self.destroy()


def main() -> None:
    Application().mainloop()
