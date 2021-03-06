/// <reference path="../../Scripts/typings/jquery/jquery.d.ts" />
/// <reference path="../../Scripts/typings/knockout/knockout.d.ts" />

import resource = require('models/resource');
import appUrl = require('common/appUrl');
import changeSubscription = require('models/changeSubscription');
import changesCallback = require('common/changesCallback');
import commandBase = require('commands/commandBase');
import folder = require("models/filesystem/folder");
import getSingleAuthTokenCommand = require("commands/getSingleAuthTokenCommand");
import shell = require("viewmodels/shell");

class changesApi {

    private eventsId: string;
    private coolDownWithDataLoss: number;
    private resourcePath: string;
    private connectToChangesApiTask: JQueryDeferred<any>;
    private webSocket: WebSocket;
    static isServerSupportingWebSockets: boolean = true;
    private eventSource: EventSource;
    private readyStateOpen = 1;

    private isCleanClose: boolean = false;
    private normalClosureCode = 1000;
    private normalClosureMessage = "CLOSE_NORMAL";
    static messageWasShownOnce: boolean = false;
    private sentMessages = [];

    private allDocsHandlers = ko.observableArray<changesCallback<documentChangeNotificationDto>>();
    private allIndexesHandlers = ko.observableArray<changesCallback<indexChangeNotificationDto>>();
    private allTransformersHandlers = ko.observableArray<changesCallback<transformerChangeNotificationDto>>();
    private watchedPrefixes = {};
    private allBulkInsertsHandlers = ko.observableArray<changesCallback<bulkInsertChangeNotificationDto>>();
    private allFsSyncHandlers = ko.observableArray<changesCallback<synchronizationUpdateNotification>>();
    private allFsConflictsHandlers = ko.observableArray<changesCallback<synchronizationConflictNotification>>();
    private allFsConfigHandlers = ko.observableArray<changesCallback<filesystemConfigNotification>>();
    private watchedFolders = {};
    private commandBase = new commandBase();

    constructor(private rs: resource, coolDownWithDataLoss: number = 0) {
        this.eventsId = this.makeId();
        this.coolDownWithDataLoss = coolDownWithDataLoss;
        this.resourcePath = appUrl.forResourceQuery(this.rs);
        this.connectToChangesApiTask = $.Deferred();

        if ("WebSocket" in window && changesApi.isServerSupportingWebSockets) {
            this.connect(this.connectWebSocket);
        }
        else if ("EventSource" in window) {
            this.connect(this.connectEventSource);
        }
        else {
            //The browser doesn't support nor websocket nor eventsource
            setTimeout(() => this.showWarning("Nor WebSocket nor EventSource are supported by your Browser! " +
                "Changes API is disabled. Please use a modern browser!"), 700);
            this.connectToChangesApiTask.reject();
        }
    }

    private connect(action: Function, needToReconnect: boolean = false) {
        var getTokenTask = new getSingleAuthTokenCommand(this.resourcePath).execute();

        getTokenTask
            .done((tokenObject: singleAuthToken) => {
                var token = tokenObject.Token;
                var connectionString = 'singleUseAuthToken=' + token + '&id=' + this.eventsId + '&coolDownWithDataLoss=' + this.coolDownWithDataLoss;

                action.call(this, connectionString, needToReconnect);
            })
            .fail(() => {
                // Connection has closed so try to reconnect every 3 seconds.
                setTimeout(() => this.connect(action, needToReconnect), 3 * 1000);
            });
    }

    private connectWebSocket(connectionString: string, needToReconnect: boolean) {
        var host = window.location.host;

        this.webSocket = new WebSocket('ws://' + host + this.resourcePath + '/changes/websocket?' + connectionString);

        this.webSocket.onmessage = (e) => this.onMessage(e);
        this.webSocket.onerror = (e) => {
            if (needToReconnect == false) {
                this.serverNotSupportingWebsocketsErrorHandler();
            } else {
                this.onError(e);
            }
        };
        this.webSocket.onclose = (e: CloseEvent) => {
            if (this.isCleanClose == false && changesApi.isServerSupportingWebSockets) {
                // Connection has closed uncleanly, so try to reconnect.
                this.connect(this.connectWebSocket, needToReconnect);
            }
        }
        this.webSocket.onopen = () => {
            console.log("Connected to WebSocket changes API (rs = " + this.rs.name + ")");
            this.reconnect(needToReconnect);
            needToReconnect = true;
            this.connectToChangesApiTask.resolve();
        }
    }

    private connectEventSource(connectionString: string, needToReconnect: boolean) {
        this.eventSource = new EventSource(this.resourcePath + '/changes/events?' + connectionString);

        this.eventSource.onmessage = (e) => this.onMessage(e);
        this.eventSource.onerror = (e) => {
            if (needToReconnect == false) {
                this.connectToChangesApiTask.reject();
            } else {
                this.onError(e);
                this.eventSource.close();
                this.connect(this.connectEventSource, needToReconnect);
            }
        };
        this.eventSource.onopen = () => {
            console.log("Connected to EventSource changes API (rs = " + this.rs.name + ")");
            this.reconnect(needToReconnect);
            needToReconnect = true;
            this.connectToChangesApiTask.resolve();
        }
    }

    private showWarning(message: string) {
        if (changesApi.messageWasShownOnce == false) {
            this.commandBase.reportWarning(message);
            changesApi.messageWasShownOnce = true;
        }
    }

    private reconnect(needToReconnect: boolean) {
        if (needToReconnect) {
            //send changes connection args after reconnecting
            this.sentMessages.forEach(args => this.send(args.command, args.value, false));
            
            ko.postbox.publish("ChangesApiReconnected", this.rs);

            if (changesApi.messageWasShownOnce) {
                this.commandBase.reportSuccess("Successfully reconnected to changes stream!");
                changesApi.messageWasShownOnce = false;
            }
        }
    }

    private onError(e: Event) {
        if (changesApi.messageWasShownOnce == false) {
            this.commandBase.reportError('Changes stream was disconnected. Retrying connection shortly.');
            changesApi.messageWasShownOnce = true;
        }
    }

    private serverNotSupportingWebsocketsErrorHandler() {
        changesApi.isServerSupportingWebSockets = false;

        if ("EventSource" in window) {
            this.showWarning("Your server doesn't support the WebSocket protocol. EventSource API is going to be used instead. " +
                "However, multi tab usage isn't supported.");
            changesApi.messageWasShownOnce = false;
            this.connect(this.connectEventSource);
        } else {
            this.showWarning("Your server doesn't support the WebSocket protocol and your browser doesn't support the EventSource API. " +
                "Changes API is Disabled. In order to use it, please use a browser that supports the EventSource API.");
            this.connectToChangesApiTask.reject();
        }
    }

    private send(command: string, value?: string, needToSaveSentMessages: boolean = true) {
        this.connectToChangesApiTask.done(() => {
            var args = {
                id: this.eventsId,
                command: command
            };
            if (value !== undefined) {
                args["value"] = value;
            }

            //TODO: exception handling?
            this.commandBase.query('/changes/config', args, this.rs)
                .done(() => this.saveSentMessages(needToSaveSentMessages, command, args));
        });
    }

    private saveSentMessages(needToSaveSentMessages: boolean, command: string, args) {
        if (needToSaveSentMessages) {
            if (command.slice(0, 2) == "un") {
                var commandName = command.slice(2, command.length);
                this.sentMessages = this.sentMessages.filter(msg => msg.command != commandName);
            } else {
                this.sentMessages.push(args);
            }
        }
    }

    private fireEvents<T>(events: Array<any>, param: T, filter: (T) => boolean) {
        for (var i = 0; i < events.length; i++) {
            if (filter(param)) {
                events[i].fire(param);
            }
        }
    }

    private onMessage(e: any) {
        var eventDto: changesApiEventDto = JSON.parse(e.data);
        var type = eventDto.Type;
        var value = eventDto.Value;

        if (type !== "Heartbeat") { // ignore heartbeat
            if (type === "DocumentChangeNotification") {
                this.fireEvents(this.allDocsHandlers(), value, (e) => true);
                for (var key in this.watchedPrefixes) {
                    var docCallbacks = <KnockoutObservableArray<documentChangeNotificationDto>> this.watchedPrefixes[key];
                    this.fireEvents(docCallbacks(), value, (e) => e.Id != null && e.Id.match("^" + key));
                }
            } else if (type === "IndexChangeNotification") {
                this.fireEvents(this.allIndexesHandlers(), value, (e) => true);
            } else if (type === "TransformerChangeNotification") {
                this.fireEvents(this.allTransformersHandlers(), value, (e) => true);
            } else if (type === "BulkInsertChangeNotification") {
                this.fireEvents(this.allBulkInsertsHandlers(), value, (e) => true);
            } else if (type === "SynchronizationUpdateNotification") {
                this.fireEvents(this.allFsSyncHandlers(), value, (e) => true);
            } else if (type === "ConflictNotification") {
                this.fireEvents(this.allFsConflictsHandlers(), value, (e) => true);
            } else if (type === "FileChangeNotification") {
                for (var key in this.watchedFolders) {
                    var folderCallbacks = <KnockoutObservableArray<fileChangeNotification>> this.watchedFolders[key];
                    this.fireEvents(folderCallbacks(), value, (e) => {
                        var notifiedFolder = folder.getFolderFromFilePath(e.File);
                        var match: string[] = null;
                        if (notifiedFolder && notifiedFolder.path) {
                            match = notifiedFolder.path.match(key);
                        }
                        return match && match.length > 0;
                    });
                }
            } else if (type === "ConfigurationChangeNotification") {
                this.fireEvents(this.allFsConfigHandlers(), value, (e) => true);
            } else {
                console.log("Unhandled Changes API notification type: " + type);
            }
        }
    }

    watchAllIndexes(onChange: (e: indexChangeNotificationDto) => void) {
        var callback = new changesCallback<indexChangeNotificationDto>(onChange);
        if (this.allIndexesHandlers().length == 0) {
            this.send('watch-indexes');
        }
        this.allIndexesHandlers.push(callback);
        return new changeSubscription(() => {
            this.allIndexesHandlers.remove(callback);
            if (this.allIndexesHandlers().length == 0) {
                this.send('unwatch-indexes');
            }
        });
    }

    watchAllTransformers(onChange: (e: transformerChangeNotificationDto) => void) {
        var callback = new changesCallback<transformerChangeNotificationDto>(onChange);
        if (this.allTransformersHandlers().length == 0) {
            this.send('watch-transformers');
        }
        this.allTransformersHandlers.push(callback);
        return new changeSubscription(() => {
            this.allTransformersHandlers.remove(callback);
            if (this.allTransformersHandlers().length == 0) {
                this.send('unwatch-transformers');
            }
        });
    }

    watchAllDocs(onChange: (e: documentChangeNotificationDto) => void) {
        var callback = new changesCallback<documentChangeNotificationDto>(onChange);
        if (this.allDocsHandlers().length == 0) {
            this.send('watch-docs');
        }
        this.allDocsHandlers.push(callback);
        return new changeSubscription(() => {
            this.allDocsHandlers.remove(callback);
            if (this.allDocsHandlers().length == 0) {
                this.send('unwatch-docs');
            }
        });
    }

    watchDocsStartingWith(docIdPrefix: string, onChange: (e: documentChangeNotificationDto) => void): changeSubscription {
        var callback = new changesCallback<documentChangeNotificationDto>(onChange);
        if (typeof (this.watchedPrefixes[docIdPrefix]) === "undefined") {
            this.send('watch-prefix', docIdPrefix);
            this.watchedPrefixes[docIdPrefix] = ko.observableArray();
        }
        this.watchedPrefixes[docIdPrefix].push(callback);

        return new changeSubscription(() => {
            this.watchedPrefixes[docIdPrefix].remove(callback);
            if (this.watchedPrefixes[docIdPrefix]().length == 0) {
                delete this.watchedPrefixes[docIdPrefix];
                this.send('unwatch-prefix', docIdPrefix);
            }
        });
    }

    watchBulks(onChange: (e: bulkInsertChangeNotificationDto) => void) {
        var callback = new changesCallback<bulkInsertChangeNotificationDto>(onChange);
        if (this.allBulkInsertsHandlers().length == 0) {
            this.send('watch-bulk-operation');
        }
        this.allBulkInsertsHandlers.push(callback);
        return new changeSubscription(() => {
            this.allBulkInsertsHandlers.remove(callback);
            if (this.allDocsHandlers().length == 0) {
                this.send('unwatch-bulk-operation');
            }
        });
    }

    watchDocPrefix(onChange: (e: documentChangeNotificationDto) => void, prefix?: string) {
        var callback = new changesCallback<documentChangeNotificationDto>(onChange);
        if (this.allDocsHandlers().length == 0) {
            this.send('watch-prefix', prefix);
        }
        this.allDocsHandlers.push(callback);
        return new changeSubscription(() => {
            this.allDocsHandlers.remove(callback);
            if (this.allDocsHandlers().length == 0) {
                this.send('unwatch-prefix', prefix);
            }
        });
    }

    watchFsSync(onChange: (e: synchronizationUpdateNotification) => void): changeSubscription {
        var callback = new changesCallback<synchronizationUpdateNotification>(onChange);
        if (this.allFsSyncHandlers().length == 0) {
            this.send('watch-sync');
        }
        this.allFsSyncHandlers.push(callback);
        return new changeSubscription(() => {
            this.allFsSyncHandlers.remove(callback);
            if (this.allFsSyncHandlers().length == 0) {
                this.send('unwatch-sync');
            }
        });
    }

    watchFsConflicts(onChange: (e: synchronizationConflictNotification) => void) : changeSubscription {
        var callback = new changesCallback<synchronizationConflictNotification>(onChange);
        if (this.allFsConflictsHandlers().length == 0) {
            this.send('watch-conflicts');
        }
        this.allFsConflictsHandlers.push(callback);
        return new changeSubscription(() => {
            this.allFsConflictsHandlers.remove(callback);
            if (this.allFsConflictsHandlers().length == 0) {
                this.send('unwatch-conflicts');
            }
        });
    }

    watchFsFolders(folder: string, onChange: (e: fileChangeNotification) => void): changeSubscription {
        var callback = new changesCallback<fileChangeNotification>(onChange);
        if (typeof (this.watchedFolders[folder]) === "undefined") {
            this.send('watch-folder', folder);
            this.watchedFolders[folder] = ko.observableArray();
        }
        this.watchedFolders[folder].push(callback);
        return new changeSubscription(() => {
            this.watchedFolders[folder].remove(callback);
            if (this.watchedFolders[folder].length == 0) {
                delete this.watchedFolders[folder];
                this.send('unwatch-folder', folder);
            }
        });
    }

    watchFsConfig(onChange: (e: filesystemConfigNotification) => void): changeSubscription {
        var callback = new changesCallback<filesystemConfigNotification>(onChange);
        if (this.allFsConfigHandlers().length == 0) {
            this.send('watch-config');
        }
        this.allFsConfigHandlers.push(callback);
        return new changeSubscription(() => {
            this.allFsConfigHandlers.remove(callback);
            if (this.allFsConfigHandlers().length == 0) {
                this.send('unwatch-config');
            }
        });
    }
    
    dispose() {
        this.connectToChangesApiTask.done(() => {
            var isCloseNeeded: boolean;

            if (isCloseNeeded = this.webSocket && this.webSocket.readyState == this.readyStateOpen){
                console.log("Disconnecting from WebSocket changes API for (rs = " + this.rs.name + ")");
                this.webSocket.close(this.normalClosureCode, this.normalClosureMessage);
            }
            else if (isCloseNeeded = this.eventSource && this.eventSource.readyState == this.readyStateOpen) {
                console.log("Disconnecting from EventSource changes API for (rs = " + this.rs.name + ")");
                this.eventSource.close();
            }

            if (isCloseNeeded) {
                this.send('disconnect', undefined, false);
                this.isCleanClose = true;
            }
        });
    }

    private makeId() {
        var text = "";
        var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        for (var i = 0; i < 5; i++)
            text += chars.charAt(Math.floor(Math.random() * chars.length));

        return text;
    }

}

export = changesApi;