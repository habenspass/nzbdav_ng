import { type Dispatch, type ReactNode, type SetStateAction, useCallback, useMemo, useState } from "react";
import { Button, Icon, Input, ManagedSetting, NativeForm as Form, SettingsIntro, SettingsPage, Tooltip } from "~/components/ui";

type CacheSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

function SettingsCard({
    icon,
    title,
    description,
    children,
}: {
    icon: string
    title: string
    description: ReactNode
    children: ReactNode
}) {
    return (
        <section className="overflow-hidden rounded-lg border border-base-content/10 bg-base-100">
            <div className="flex items-start gap-3 border-b border-base-content/10 p-4">
                <span className="inline-flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
                    <Icon name={icon} className="!text-[20px]" />
                </span>
                <div>
                    <h2 className="text-sm font-semibold text-base-content">{title}</h2>
                    <p className="mt-0.5 text-xs leading-relaxed text-base-content/50">
                        {description}
                    </p>
                </div>
            </div>
            <div className="grid grid-cols-1 gap-4 p-4 lg:grid-cols-2">
                {children}
            </div>
        </section>
    );
}

export function CacheSettings({ config, setNewConfig }: CacheSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const enabled = (config["cache.prefetch-enabled"] ?? "false") === "true";
    const origin = typeof window === "undefined" ? "" : window.location.origin;
    const webhookToken = config["jellyfin.webhook-token"] ?? "";
    const webhookUrl = useMemo(
        () => webhookToken ? `${origin}/api/jellyfin-webhook?apikey=${webhookToken}` : "",
        [origin, webhookToken],
    );
    const [copied, setCopied] = useState(false);
    const onCopy = useCallback(async () => {
        try {
            await navigator.clipboard.writeText(webhookUrl);
            setCopied(true);
            setTimeout(() => setCopied(false), 1500);
        } catch {
            // clipboard access can be denied by the browser; the field is still selectable/copyable by hand
        }
    }, [webhookUrl]);

    return (
        <SettingsPage>
            <SettingsIntro>
                Cache the next episode of a series while you're watching the current one, so playback
                starts instantly instead of triggering a fresh Usenet download. Opt-in and disabled by
                default — enabling it alone does nothing until Jellyfin's Webhook plugin is pointed at
                the URL below.
            </SettingsIntro>

            <ManagedSetting configKeys={[
                "cache.prefetch-enabled", "cache.dir", "cache.min-free-space-gb",
                "cache.prefetch-threshold-percent", "cache.max-cache-time-hours", "cache.max-cache-episodes",
            ]}>
            <div className="flex flex-col gap-4">
            <SettingsCard
                icon="movie_filter"
                title="Predictive episode prefetch"
                description={
                    <>
                        Requires a Sonarr connection under Radarr/Sonarr settings and an organized media
                        library (symlinks or STRM files) under Library settings.
                    </>
                }
            >
                <Form.Group className="flex flex-col gap-2 lg:col-span-2">
                    <Form.Check
                        type="switch"
                        id="cache-prefetch-enabled"
                        className="cursor-pointer gap-2 p-0"
                        label="Enable episode prefetch cache"
                        checked={enabled}
                        onChange={e => set("cache.prefetch-enabled", String(e.target.checked))} />
                </Form.Group>

                <Form.Group className="flex flex-col gap-2">
                    <Form.Label>Cache directory</Form.Label>
                    <Form.Control
                        className="w-full max-w-md"
                        type="text"
                        placeholder="(default: a subdirectory under the app's config directory)"
                        disabled={!enabled}
                        value={config["cache.dir"] ?? ""}
                        onChange={e => set("cache.dir", e.target.value)} />
                </Form.Group>

                <Form.Group className="flex flex-col gap-2">
                    <Form.Label>Prefetch threshold (%)</Form.Label>
                    <Form.Control
                        className="w-full max-w-md"
                        type="number"
                        min={1}
                        max={100}
                        disabled={!enabled}
                        value={config["cache.prefetch-threshold-percent"] ?? "80"}
                        onChange={e => set("cache.prefetch-threshold-percent", e.target.value)} />
                    <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                        Start caching the next episode once this much of the current one has been
                        watched. Default 80.
                    </p>
                </Form.Group>

                <Form.Group className="flex flex-col gap-2">
                    <Form.Label>Maximum cache time (hours)</Form.Label>
                    <Form.Control
                        className="w-full max-w-md"
                        type="number"
                        min={1}
                        disabled={!enabled}
                        value={config["cache.max-cache-time-hours"] ?? "48"}
                        onChange={e => set("cache.max-cache-time-hours", e.target.value)} />
                    <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                        A cached episode older than this is evicted regardless of anything else. Default 48.
                    </p>
                </Form.Group>

                <Form.Group className="flex flex-col gap-2">
                    <Form.Label>Maximum cache episodes</Form.Label>
                    <Form.Control
                        className="w-full max-w-md"
                        type="number"
                        min={1}
                        disabled={!enabled}
                        value={config["cache.max-cache-episodes"] ?? "5"}
                        onChange={e => set("cache.max-cache-episodes", e.target.value)} />
                    <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                        Above this many cached episodes, the least-recently-watched are evicted first. Default 5.
                    </p>
                </Form.Group>

                <Form.Group className="flex flex-col gap-2">
                    <Form.Label>Minimum free space (GB)</Form.Label>
                    <Form.Control
                        className="w-full max-w-md"
                        type="number"
                        min={0}
                        disabled={!enabled}
                        value={config["cache.min-free-space-gb"] ?? "10"}
                        onChange={e => set("cache.min-free-space-gb", e.target.value)} />
                    <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                        If free space on the cache volume drops below this, cached episodes are evicted
                        (oldest-watched first) until it's restored, even below the episode cap above. Default 10.
                    </p>
                </Form.Group>
            </SettingsCard>

            <SettingsCard
                icon="webhook"
                title="Jellyfin webhook"
                description="Paste this URL into Jellyfin's Webhook plugin to trigger prefetching."
            >
                <div className="flex flex-col gap-2 lg:col-span-2">
                    <Form.Label>Webhook URL</Form.Label>
                    <div className="flex items-center gap-2">
                        <Input
                            className="flex-1 font-mono text-xs"
                            type="text"
                            readOnly
                            value={webhookUrl}
                            onFocus={e => e.currentTarget.select()} />
                        <Tooltip content="Copy the webhook URL">
                            <Button variant={copied ? "success" : "secondary"} size="xsmall" onClick={onCopy}>
                                {copied ? "Copied" : "Copy"}
                            </Button>
                        </Tooltip>
                    </div>
                    <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                        In Jellyfin: Dashboard → Plugins → Webhook → Add Generic Destination. Set Notification
                        Type to <strong>Playback Progress</strong>, Item Type to <strong>Episodes</strong>, and
                        enable <strong>Send All Properties</strong>.
                    </p>
                </div>
            </SettingsCard>
            </div>
            </ManagedSetting>
        </SettingsPage>
    );
}

export function isCacheSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["cache.prefetch-enabled"] !== newConfig["cache.prefetch-enabled"]
        || config["cache.dir"] !== newConfig["cache.dir"]
        || config["cache.min-free-space-gb"] !== newConfig["cache.min-free-space-gb"]
        || config["cache.prefetch-threshold-percent"] !== newConfig["cache.prefetch-threshold-percent"]
        || config["cache.max-cache-time-hours"] !== newConfig["cache.max-cache-time-hours"]
        || config["cache.max-cache-episodes"] !== newConfig["cache.max-cache-episodes"];
}
