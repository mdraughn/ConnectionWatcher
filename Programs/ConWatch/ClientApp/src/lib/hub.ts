// SignalR wrapper: live snapshots + transitions, resync on reconnect.
import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";
import { fetchSnapshot } from "./api";
import type { Snapshot, TransitionEvent } from "./types";

export interface HubCallbacks {
  onSnapshot: (snapshot: Snapshot) => void;
  onTransition: (transition: TransitionEvent) => void;
  onConnectionChange: (connected: boolean) => void;
}

export function startHub(callbacks: HubCallbacks): void {
  const connection = new HubConnectionBuilder()
    .withUrl("/hubs/status")
    .withAutomaticReconnect()
    .build();

  connection.on("statusChanged", (snapshot: Snapshot) => callbacks.onSnapshot(snapshot));
  connection.on("transition", (t: TransitionEvent) => callbacks.onTransition(t));

  const syncState = () =>
    callbacks.onConnectionChange(connection.state === HubConnectionState.Connected);

  connection.onreconnecting(() => syncState());
  connection.onreconnected(async () => {
    syncState();
    const snapshot = await fetchSnapshot();
    if (snapshot) callbacks.onSnapshot(snapshot);
  });
  connection.onclose(() => syncState());

  connection
    .start()
    .then(syncState)
    .catch(() => {
      syncState();
      // Retry once after a delay; automatic reconnect handles later drops.
      setTimeout(() => connection.start().then(syncState).catch(() => syncState()), 3000);
    });
}
