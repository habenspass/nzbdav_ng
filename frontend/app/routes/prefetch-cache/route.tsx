import { redirect } from "react-router";
import type { Route } from "./+types/route";
import { useCallback, useState } from "react";
import { backendClient, type PrefetchCacheStatusItem } from "~/clients/backend-client.server";
import { useWebsocketTopics } from "~/utils/shared-websocket";
import { formatFileSize } from "~/utils/file-size";
import { Badge, Button, Icon } from "~/components/ui";

const TOPIC_NAME = "pcs";

export async function loader() {
    const [config, status] = await Promise.all([
        backendClient.getConfig(["cache.prefetch-enabled"]),
        backendClient.getPrefetchCacheStatus(),
    ]);
    const isEnabled = config.find(x => x.configName === "cache.prefetch-enabled")?.configValue === "true";
    if (!isEnabled) {
        return redirect("/queue");
    }
    return { items: status };
}

export default function PrefetchCache({ loaderData }: Route.ComponentProps) {
    const [items, setItems] = useState<PrefetchCacheStatusItem[]>(loaderData.items);
    const [evictingIds, setEvictingIds] = useState<Set<string>>(() => new Set());
    const [error, setError] = useState<string | null>(null);

    const onWebsocketMessage = useCallback((_topic: string, message: string) => {
        try {
            const parsed = JSON.parse(message) as { items: PrefetchCacheStatusItem[] };
            setItems(parsed.items ?? []);
        } catch {
            // malformed/partial frame; next tick will resync
        }
    }, []);

    useWebsocketTopics({ [TOPIC_NAME]: "state" }, onWebsocketMessage);

    const onEvict = useCallback(async (id: string) => {
        setEvictingIds(prev => new Set(prev).add(id));
        setError(null);
        try {
            const resp = await fetch(`/api/evict-prefetch-cache-item?id=${encodeURIComponent(id)}`, {
                method: "POST",
            });
            if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
            setItems(prev => prev.filter(x => x.id !== id));
        } catch (e: any) {
            setError(e?.message ?? String(e));
        } finally {
            setEvictingIds(prev => {
                const next = new Set(prev);
                next.delete(id);
                return next;
            });
        }
    }, []);

    const sorted = [...items].sort((a, b) => b.startedAt - a.startedAt);

    return (
        <div className="flex min-h-full min-w-full flex-col gap-6 px-4 py-4 text-sm text-base-content/70 md:px-8">
            <div className="card border border-base-content/10 bg-base-100 shadow-sm">
                <div className="card-body gap-4 p-4 md:p-6">
                    <div>
                        <h2 className="text-base font-semibold tracking-tight text-base-content">Prefetch Cache</h2>
                        <p className="mt-1 text-xs text-base-content/50">
                            Episodes cached ahead of playback, resolved from Jellyfin's webhook or triggered manually.
                        </p>
                    </div>
                    {error && (
                        <div className="alert alert-soft alert-error text-xs">{error}</div>
                    )}
                </div>
            </div>

            {sorted.length === 0 ? (
                <div className="card border border-base-content/10 bg-base-100 shadow-sm">
                    <div className="card-body items-center py-12 text-center text-base-content/50">
                        Nothing cached yet. Cross the configured playback threshold in Jellyfin, or use
                        "Cache this now" from the Files page, to see entries here.
                    </div>
                </div>
            ) : (
                <div className="card overflow-hidden border border-base-content/10 bg-base-100 shadow-sm">
                    <div className="overflow-x-auto">
                        <table className="table table-sm w-full text-xs">
                            <thead>
                                <tr>
                                    <th className="px-3 py-2 text-left text-[10px] font-semibold uppercase tracking-wider text-base-content/50">Episode</th>
                                    <th className="px-3 py-2 text-left text-[10px] font-semibold uppercase tracking-wider text-base-content/50">Status</th>
                                    <th className="px-3 py-2 text-left text-[10px] font-semibold uppercase tracking-wider text-base-content/50">Size</th>
                                    <th className="px-3 py-2 text-left text-[10px] font-semibold uppercase tracking-wider text-base-content/50">Started</th>
                                    <th className="px-3 py-2 text-left text-[10px] font-semibold uppercase tracking-wider text-base-content/50">Last accessed</th>
                                    <th className="px-3 py-2 text-right text-[10px] font-semibold uppercase tracking-wider text-base-content/50">Actions</th>
                                </tr>
                            </thead>
                            <tbody>
                                {sorted.map(item => (
                                    <Row
                                        key={item.id}
                                        item={item}
                                        isEvicting={evictingIds.has(item.id)}
                                        onEvict={() => onEvict(item.id)}
                                    />
                                ))}
                            </tbody>
                        </table>
                    </div>
                </div>
            )}
        </div>
    );
}

function Row({
    item,
    isEvicting,
    onEvict,
}: {
    item: PrefetchCacheStatusItem,
    isEvicting: boolean,
    onEvict: () => void,
}) {
    const hasEpisodeInfo = item.seasonNumber > 0 || item.episodeNumber > 0;
    const label = hasEpisodeInfo
        ? `${item.seriesName} S${pad(item.seasonNumber)}E${pad(item.episodeNumber)}`
        : item.seriesName;

    return (
        <tr>
            <td className="max-w-md truncate px-3 py-2 align-middle font-medium text-base-content" title={label}>
                {label}
            </td>
            <td className="px-3 py-2 align-middle">
                <StatusBadge item={item} />
            </td>
            <td className="whitespace-nowrap px-3 py-2 align-middle tabular-nums text-base-content/60">
                {formatFileSize(item.fileSize)}
            </td>
            <td className="whitespace-nowrap px-3 py-2 align-middle tabular-nums text-base-content/50" title={new Date(item.startedAt).toLocaleString()}>
                {formatAge(item.startedAt)}
            </td>
            <td className="whitespace-nowrap px-3 py-2 align-middle tabular-nums text-base-content/50" title={new Date(item.lastAccessedAt).toLocaleString()}>
                {formatAge(item.lastAccessedAt)}
            </td>
            <td className="whitespace-nowrap px-3 py-2 text-right align-middle">
                <Button variant="secondary" size="xsmall" disabled={isEvicting} onClick={onEvict}>
                    <Icon name="delete" className="!text-[14px]" />
                    {isEvicting ? "Evicting…" : "Evict"}
                </Button>
            </td>
        </tr>
    );
}

function StatusBadge({ item }: { item: PrefetchCacheStatusItem }) {
    if (item.status === "Complete") {
        return <Badge className="badge-success badge-sm uppercase">Cached</Badge>;
    }
    if (item.status === "InProgress") {
        return <Badge className="badge-warning badge-sm uppercase">Caching…</Badge>;
    }
    return (
        <Badge className="badge-error badge-sm uppercase" title={item.failureReason ?? undefined}>
            Failed
        </Badge>
    );
}

function pad(n: number): string {
    return String(n).padStart(2, "0");
}

function formatAge(unixMillis: number): string {
    const age = Math.max(0, Math.floor((Date.now() - unixMillis) / 1000));
    if (age < 5) return "just now";
    if (age < 60) return `${age}s ago`;
    if (age < 3600) return `${Math.floor(age / 60)}m ago`;
    if (age < 86400) return `${Math.floor(age / 3600)}h ago`;
    return `${Math.floor(age / 86400)}d ago`;
}
