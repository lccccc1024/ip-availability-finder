"""Small Material 3 inspired Tk widgets without a runtime UI dependency."""
from __future__ import annotations

import tkinter as tk
from collections.abc import Callable


BACKGROUND = "#F8F7FD"
SURFACE = "#FFFFFF"
SURFACE_SOFT = "#F2F0F8"
FIELD = "#F4F3FA"
PRIMARY = "#5854B8"
PRIMARY_HOVER = "#4843A6"
PRIMARY_CONTAINER = "#E9E7FF"
ON_PRIMARY_CONTAINER = "#28246B"
TEXT = "#1C1B24"
MUTED = "#686775"
OUTLINE = "#D8D6E2"
WARNING_CONTAINER = "#FFF2DB"
WARNING_TEXT = "#72501D"
FONT = "Microsoft YaHei UI"


def rounded_rectangle(canvas: tk.Canvas, x1: int, y1: int, x2: int, y2: int, radius: int, fill: str, tag: str = "shape") -> None:
    """Draw a solid rounded rectangle from simple Canvas primitives."""
    if x2 <= x1 or y2 <= y1:
        return
    radius = max(0, min(radius, (x2 - x1) // 2, (y2 - y1) // 2))
    if radius == 0:
        canvas.create_rectangle(x1, y1, x2, y2, fill=fill, outline=fill, tags=tag)
        return
    canvas.create_rectangle(x1 + radius, y1, x2 - radius, y2, fill=fill, outline=fill, tags=tag)
    canvas.create_rectangle(x1, y1 + radius, x2, y2 - radius, fill=fill, outline=fill, tags=tag)
    for cx in (x1, x2 - 2 * radius):
        for cy in (y1, y2 - 2 * radius):
            canvas.create_oval(cx, cy, cx + 2 * radius, cy + 2 * radius, fill=fill, outline=fill, tags=tag)


class RoundedCard(tk.Frame):
    def __init__(
        self,
        master: tk.Misc,
        *,
        fill: str = SURFACE,
        outside: str = BACKGROUND,
        radius: int = 24,
        padding: int = 18,
    ) -> None:
        super().__init__(master, bg=outside, highlightthickness=0)
        self._fill = fill
        self._radius = radius
        self._background = tk.Canvas(self, bg=outside, highlightthickness=0, borderwidth=0)
        self._background.place(x=0, y=0, relwidth=1, relheight=1)
        self._background.bind("<Configure>", self._draw)
        self.content = tk.Frame(self, bg=fill)
        self.content.pack(fill="both", expand=True, padx=padding, pady=padding)
        self._background.tk.call("lower", self._background._w)

    def _draw(self, event: tk.Event) -> None:
        self._background.delete("shape")
        rounded_rectangle(self._background, 0, 0, event.width - 1, event.height - 1, self._radius, self._fill)


class MaterialField(tk.Frame):
    def __init__(self, master: tk.Misc, *, label: str, variable: tk.StringVar, hint: str = "", background: str = SURFACE) -> None:
        super().__init__(master, bg=background)
        tk.Label(self, text=label, bg=background, fg=TEXT, font=(FONT, 9, "bold"), anchor="w").pack(fill="x", pady=(0, 5))
        self.shell = tk.Canvas(self, width=180, height=43, bg=background, highlightthickness=0, borderwidth=0)
        self.shell.pack(fill="x")
        self.entry = tk.Entry(
            self.shell,
            textvariable=variable,
            bg=FIELD,
            fg=TEXT,
            disabledbackground=FIELD,
            disabledforeground=MUTED,
            insertbackground=PRIMARY,
            borderwidth=0,
            highlightthickness=0,
            font=(FONT, 10),
            relief="flat",
        )
        self._window = self.shell.create_window(14, 22, window=self.entry, anchor="w")
        self.shell.bind("<Configure>", self._redraw)
        self.entry.bind("<FocusIn>", self._redraw)
        self.entry.bind("<FocusOut>", self._redraw)
        tk.Label(self, text=hint, bg=background, fg=MUTED, font=(FONT, 8), anchor="w").pack(fill="x", pady=(3, 0))

    def _redraw(self, _event: tk.Event | None = None) -> None:
        width = self.shell.winfo_width()
        self.shell.delete("shape")
        focused = self.focus_get() is self.entry
        if focused:
            rounded_rectangle(self.shell, 0, 0, width - 1, 42, 14, PRIMARY)
            rounded_rectangle(self.shell, 2, 2, width - 3, 40, 12, FIELD)
        else:
            rounded_rectangle(self.shell, 0, 0, width - 1, 42, 14, FIELD)
        self.shell.tag_lower("shape")
        self.shell.itemconfigure(self._window, width=max(1, width - 28))


class PillButton(tk.Canvas):
    def __init__(
        self,
        master: tk.Misc,
        *,
        text: str,
        command: Callable[[], None],
        width: int = 110,
        height: int = 42,
        variant: str = "tonal",
        background: str = SURFACE,
        enabled: bool = True,
    ) -> None:
        super().__init__(master, width=width, height=height, bg=background, highlightthickness=0, borderwidth=0, takefocus=1)
        self._text = text
        self._command = command
        self._variant = variant
        self._enabled = enabled
        self._hovered = False
        self.configure(cursor="hand2" if enabled else "arrow")
        self.bind("<Configure>", self._draw)
        self.bind("<Enter>", self._enter)
        self.bind("<Leave>", self._leave)
        self.bind("<Button-1>", self._activate)
        self.bind("<Key-Return>", self._activate)
        self.bind("<Key-space>", self._activate)
        self.bind("<FocusIn>", self._draw)
        self.bind("<FocusOut>", self._draw)

    def set_enabled(self, enabled: bool) -> None:
        self._enabled = enabled
        self.configure(cursor="hand2" if enabled else "arrow")
        self._draw()

    def _enter(self, _event: tk.Event) -> None:
        self._hovered = True
        self._draw()

    def _leave(self, _event: tk.Event) -> None:
        self._hovered = False
        self._draw()

    def _activate(self, _event: tk.Event) -> None:
        if self._enabled:
            self._command()

    def _draw(self, _event: tk.Event | None = None) -> None:
        self.delete("all")
        width, height = self.winfo_width(), self.winfo_height()
        if self._enabled:
            if self._variant == "filled":
                fill = PRIMARY_HOVER if self._hovered else PRIMARY
                foreground = SURFACE
            elif self._variant == "text":
                fill = PRIMARY_CONTAINER if self._hovered else SURFACE
                foreground = PRIMARY
            else:
                fill = "#DCDAFA" if self._hovered else PRIMARY_CONTAINER
                foreground = ON_PRIMARY_CONTAINER
        else:
            fill = "#EFEEF4"
            foreground = "#A2A0AB"
        if self.focus_get() is self and self._enabled:
            rounded_rectangle(self, 0, 0, width - 1, height - 1, height // 2, ON_PRIMARY_CONTAINER)
            rounded_rectangle(self, 3, 3, width - 4, height - 4, height // 2 - 3, fill)
        else:
            rounded_rectangle(self, 1, 1, width - 2, height - 2, height // 2 - 1, fill)
        self.create_text(width // 2, height // 2, text=self._text, fill=foreground, font=(FONT, 10, "bold"))
