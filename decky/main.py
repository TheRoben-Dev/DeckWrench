"""DeckWrench's Decky Loader backend: runs the DeckWrench helper (`deckwrench`) that the app installs."""
import asyncio
import json
import os
import subprocess

import decky


def _helper() -> str | None:
    home = decky.DECKY_USER_HOME
    for path in (
        os.path.join(home, "Applications", "DeckWrench", "deckwrench"),
        os.path.join(home, ".local", "bin", "deckwrench"),
    ):
        if os.access(path, os.X_OK):
            return path
    return None


def _env() -> dict:
    env = dict(os.environ)
    env["HOME"] = decky.DECKY_USER_HOME
    env.pop("LD_LIBRARY_PATH", None)  # Decky's own libraries confuse the helper
    return env


class Plugin:
    async def status(self) -> dict:
        """Home's findings, drives and pending Steam changes, or why they can't be read."""
        helper = _helper()
        if helper is None:
            return {"installed": False}
        proc = await asyncio.create_subprocess_exec(
            helper, "status", "--json",
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE, env=_env(),
        )
        try:
            out, err = await asyncio.wait_for(proc.communicate(), timeout=120)
        except asyncio.TimeoutError:
            proc.kill()
            return {"installed": True, "error": "DeckWrench took too long to scan."}
        if proc.returncode != 0:
            decky.logger.error("deckwrench status: %s", err.decode(errors="replace"))
            return {"installed": True, "error": "DeckWrench couldn't scan. Open the app for details."}
        data = json.loads(out.decode())
        data["installed"] = True
        return data

    async def apply_pending(self) -> bool:
        """Starts the helper that writes waiting Steam changes the moment Steam exits; the frontend then restarts Steam."""
        helper = _helper()
        if helper is None:
            return False
        subprocess.Popen(
            [helper, "apply-pending", "--wait"],
            env=_env(), start_new_session=True,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        return True

    async def _main(self):
        decky.logger.info("DeckWrench plugin loaded; helper: %s", _helper())

    async def _unload(self):
        pass
