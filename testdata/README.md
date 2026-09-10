Local-only real-data snapshots. NEVER commit a database here: the Settings table contains the MQTT broker password.

To refresh: stop the conwatch project on the NAS, download docker/conwatch/data/conwatch.db via File Station, save it here as nas-snapshot.db, restart the project. Tests in RealDataSnapshotTests pick it up automatically (they skip when it is absent).
