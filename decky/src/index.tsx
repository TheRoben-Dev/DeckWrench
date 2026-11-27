import { ButtonItem, Field, PanelSection, PanelSectionRow, Spinner, staticClasses } from "@decky/ui";
import { callable, definePlugin, toaster } from "@decky/api";
import { useEffect, useState } from "react";
import { FaWrench } from "react-icons/fa";

type Finding = { level: "problem" | "attention" | "good"; title: string; detail: string };
type Drive = { label: string; free: number; total: number };
type Status = {
  installed: boolean;
  error?: string;
  version?: string;
  summary?: string;
  findings?: Finding[];
  drives?: Drive[];
  reclaimable?: number;
  pending?: string[];
  gameId?: string;
};

const getStatus = callable<[], Status>("status");
const applyPending = callable<[], boolean>("apply_pending");

const size = (b: number) => {
  const units = ["B", "KB", "MB", "GB", "TB"];
  let i = 0;
  while (b >= 1024 && i < units.length - 1) {
    b /= 1024;
    i++;
  }
  return `${b.toFixed(b >= 10 || i === 0 ? 0 : 1)} ${units[i]}`;
};

const colour = { problem: "#ff6b6b", attention: "#f5b041", good: "#58d68d" } as const;

function openDeckWrench(s: Status) {
  if (s.gameId) {
    SteamClient.Apps.RunGame(s.gameId, "", -1, 100);
  }
}

function Content() {
  const [status, setStatus] = useState<Status | null>(null);
  const [busy, setBusy] = useState(false);

  const load = async () => {
    setBusy(true);
    try {
      setStatus(await getStatus());
    } finally {
      setBusy(false);
    }
  };

  useEffect(() => {
    load();
  }, []);

  if (status === null) {
    return (
      <PanelSection>
        <PanelSectionRow>
          <Field label="Checking your Deck…">
            <Spinner />
          </Field>
        </PanelSectionRow>
      </PanelSection>
    );
  }

  if (!status.installed) {
    return (
      <PanelSection title="DeckWrench isn't installed">
        <PanelSectionRow>
          <Field description="Install the DeckWrench app first (github.com/TheRoben-Dev/DeckWrench). This panel shows what it finds." />
        </PanelSectionRow>
      </PanelSection>
    );
  }

  const findings = status.findings ?? [];
  const pending = status.pending ?? [];
  return (
    <>
      <PanelSection title={status.summary ?? "DeckWrench"}>
        {status.error && (
          <PanelSectionRow>
            <Field description={status.error} />
          </PanelSectionRow>
        )}
        {findings.slice(0, 5).map((f, i) => (
          <PanelSectionRow key={i}>
            <Field label={<span style={{ color: colour[f.level] }}>{f.title}</span>} description={f.detail} bottomSeparator="none" />
          </PanelSectionRow>
        ))}
        <PanelSectionRow>
          <ButtonItem layout="below" onClick={() => openDeckWrench(status)} disabled={!status.gameId}>
            {findings.some((f) => f.level !== "good") ? "Fix in DeckWrench" : "Open DeckWrench"}
          </ButtonItem>
        </PanelSectionRow>
      </PanelSection>

      {pending.length > 0 && (
        <PanelSection title="Waiting for Steam to restart">
          {pending.map((p, i) => (
            <PanelSectionRow key={i}>
              <Field description={p} bottomSeparator="none" />
            </PanelSectionRow>
          ))}
          <PanelSectionRow>
            <ButtonItem
              layout="below"
              onClick={async () => {
                if (await applyPending()) {
                  toaster.toast({ title: "DeckWrench", body: "Restarting Steam to apply the changes…" });
                  setTimeout(() => SteamClient.User.StartRestart(false), 500);
                }
              }}
            >
              Apply now (restarts Steam)
            </ButtonItem>
          </PanelSectionRow>
        </PanelSection>
      )}

      <PanelSection title="Storage">
        {(status.drives ?? []).map((d, i) => (
          <PanelSectionRow key={i}>
            <Field label={d.label} bottomSeparator="none">
              {size(d.free)} free of {size(d.total)}
            </Field>
          </PanelSectionRow>
        ))}
        {(status.reclaimable ?? 0) > 0 && (
          <PanelSectionRow>
            <Field description={`${size(status.reclaimable!)} can be freed in DeckWrench → Storage.`} />
          </PanelSectionRow>
        )}
        <PanelSectionRow>
          <ButtonItem layout="below" onClick={load} disabled={busy}>
            {busy ? "Scanning…" : "Scan again"}
          </ButtonItem>
        </PanelSectionRow>
      </PanelSection>
    </>
  );
}

export default definePlugin(() => ({
  name: "DeckWrench",
  titleView: <div className={staticClasses.Title}>DeckWrench</div>,
  content: <Content />,
  icon: <FaWrench />,
}));
