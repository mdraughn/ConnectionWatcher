ConnectionWatcher - manual install (Windows, x64)

1. Unzip anywhere, for example C:\Program Files\DSoftCo\ConnectionWatcher.
2. From a terminal in that folder:

     conwatch service install --port 8080
     conwatch tray install
     conwatch service status

   The first command asks for administrator rights once. It registers the
   ConnectionWatcher service, opens the firewall for the port, adds a Start
   Menu shortcut to the dashboard, and starts the service.

Everything else:   conwatch --help

Config:  %ProgramData%\DSoftCo\ConnectionWatcher\conwatch.json
Data:    %ProgramData%\DSoftCo\ConnectionWatcher\data
Tray:    %AppData%\DSoftCo\ConnectionWatcher\tray.json   (per user)

Change the port later (re-registers the firewall rule and shortcut, restarts the service):
     conwatch service install --port 9090

Remove:  conwatch service uninstall
         conwatch tray uninstall
         then delete the folder. Data and config are left in place.
