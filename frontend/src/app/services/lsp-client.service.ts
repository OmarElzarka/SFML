import { Injectable } from '@angular/core';

declare const monaco: any;

@Injectable({
  providedIn: 'root'
})
export class LspClientService {
  private socket: WebSocket | null = null;
  private messageId = 1;
  private pendingRequests = new Map<number, { resolve: (value: any) => void; reject: (reason: any) => void }>();
  private currentProjectId: number | null = null;
  private isInitialized = false;
  private openDocuments = new Map<string, number>(); // uri -> version
  private providersRegistered = false;

  // Active files mapping: fileName -> monaco model
  private activeModels = new Map<string, any>();

  constructor() {}

  /**
   * Connects to the backend LSP WebSocket for the specified project.
   */
  public connect(projectId: number): void {
    if (this.currentProjectId === projectId && this.socket && this.socket.readyState === WebSocket.OPEN) {
      return;
    }

    this.disconnect();
    this.currentProjectId = projectId;
    this.isInitialized = false;
    this.openDocuments.clear();

    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    // Connect directly to port 5000 if running locally in Angular CLI (4200), or current host
    let portPart = '';
    if (window.location.port === '4200') {
      portPart = ':5000';
    } else if (window.location.port && window.location.port !== '80' && window.location.port !== '443') {
      portPart = `:${window.location.port}`;
    }
    const url = `${protocol}//${window.location.hostname}${portPart}/ws/lsp/${projectId}`;

    console.log(`[LSP] Connecting to ${url}...`);

    try {
      this.socket = new WebSocket(url);

      this.socket.onopen = () => {
        console.log(`[LSP] Connected to Language Server for project ${projectId}`);
        this.sendInitialize();
      };

      this.socket.onmessage = (event) => {
        this.handleMessage(event.data);
      };

      this.socket.onerror = (err) => {
        console.warn('[LSP] WebSocket error:', err);
      };

      this.socket.onclose = (event) => {
        console.log(`[LSP] WebSocket closed (code ${event.code}): ${event.reason}`);
        this.isInitialized = false;
        // Reject all pending requests
        this.pendingRequests.forEach(req => req.reject(new Error('LSP connection closed')));
        this.pendingRequests.clear();
      };
    } catch (err) {
      console.error('[LSP] Failed to create WebSocket connection:', err);
    }
  }

  public disconnect(): void {
    if (this.socket) {
      try {
        this.socket.close();
      } catch { }
      this.socket = null;
    }
    this.isInitialized = false;
    this.currentProjectId = null;
    this.pendingRequests.clear();
  }

  /**
   * Registers native Monaco language providers for C++ and SFML 2.6.2.
   */
  public registerMonacoProviders(): void {
    if (this.providersRegistered || typeof monaco === 'undefined') {
      return;
    }
    this.providersRegistered = true;

    console.log('[LSP] Registering C++ Language Providers for Monaco...');

    // 1. Completion Provider (Prefix, Member/Dot, Namespace, Parameter Hints)
    monaco.languages.registerCompletionItemProvider('cpp', {
      triggerCharacters: ['.', ':', '>', '<', '"', '/', '(', ' '],
      provideCompletionItems: async (model: any, position: any, context: any) => {
        if (!this.isReady()) {
          return { suggestions: [] };
        }

        const uri = this.getModelUri(model);
        const line = position.lineNumber - 1;
        const character = position.column - 1;

        try {
          const response = await this.sendRequest('textDocument/completion', {
            textDocument: { uri },
            position: { line, character },
            context: {
              triggerKind: context.triggerKind === 2 ? 2 : 1,
              triggerCharacter: context.triggerCharacter
            }
          });

          if (!response) {
            return { suggestions: [] };
          }

          const items: any[] = Array.isArray(response) ? response : (response.items || []);

          const kindMap: Record<number, number> = {
            1: monaco.languages.CompletionItemKind.Text,
            2: monaco.languages.CompletionItemKind.Method,
            3: monaco.languages.CompletionItemKind.Function,
            4: monaco.languages.CompletionItemKind.Constructor,
            5: monaco.languages.CompletionItemKind.Field,
            6: monaco.languages.CompletionItemKind.Variable,
            7: monaco.languages.CompletionItemKind.Class,
            8: monaco.languages.CompletionItemKind.Interface,
            9: monaco.languages.CompletionItemKind.Module,
            10: monaco.languages.CompletionItemKind.Property,
            11: monaco.languages.CompletionItemKind.Unit,
            12: monaco.languages.CompletionItemKind.Value,
            13: monaco.languages.CompletionItemKind.Enum,
            14: monaco.languages.CompletionItemKind.Keyword,
            15: monaco.languages.CompletionItemKind.Snippet,
            16: monaco.languages.CompletionItemKind.Color,
            17: monaco.languages.CompletionItemKind.File,
            18: monaco.languages.CompletionItemKind.Reference,
            19: monaco.languages.CompletionItemKind.Folder,
            20: monaco.languages.CompletionItemKind.EnumMember,
            21: monaco.languages.CompletionItemKind.Constant,
            22: monaco.languages.CompletionItemKind.Struct,
            23: monaco.languages.CompletionItemKind.Event,
            24: monaco.languages.CompletionItemKind.Operator,
            25: monaco.languages.CompletionItemKind.TypeParameter
          };

          const word = model.getWordUntilPosition(position);
          const range = {
            startLineNumber: position.lineNumber,
            endLineNumber: position.lineNumber,
            startColumn: word.startColumn,
            endColumn: word.endColumn
          };

          const suggestions = items.map(item => {
            const isSnippet = item.insertTextFormat === 2;
            let insertText = item.textEdit ? item.textEdit.newText : (item.insertText || item.label);

            let doc = '';
            if (item.documentation) {
              doc = typeof item.documentation === 'string' ? item.documentation : item.documentation.value;
            }

            return {
              label: item.label,
              kind: kindMap[item.kind] || monaco.languages.CompletionItemKind.Property,
              detail: item.detail || '',
              documentation: doc ? { value: doc, isTrusted: true } : undefined,
              insertText: insertText,
              insertTextRules: isSnippet ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
              range: range,
              sortText: item.sortText || item.label,
              filterText: item.filterText || item.label
            };
          });

          return { suggestions };
        } catch (err) {
          return { suggestions: [] };
        }
      }
    });

    // 2. Signature Help Provider (Parameters, Overload Cycling)
    monaco.languages.registerSignatureHelpProvider('cpp', {
      signatureHelpTriggerCharacters: ['(', ','],
      signatureHelpRetriggerCharacters: [','],
      provideSignatureHelp: async (model: any, position: any, token: any, context: any) => {
        if (!this.isReady()) {
          return null;
        }

        const uri = this.getModelUri(model);
        const line = position.lineNumber - 1;
        const character = position.column - 1;

        try {
          const res = await this.sendRequest('textDocument/signatureHelp', {
            textDocument: { uri },
            position: { line, character }
          });

          if (!res || !res.signatures || res.signatures.length === 0) {
            return null;
          }

          return {
            value: {
              signatures: res.signatures.map((s: any) => {
                let doc = '';
                if (s.documentation) {
                  doc = typeof s.documentation === 'string' ? s.documentation : s.documentation.value;
                }
                return {
                  label: s.label,
                  documentation: doc ? { value: doc } : undefined,
                  parameters: (s.parameters || []).map((p: any) => {
                    let pDoc = '';
                    if (p.documentation) {
                      pDoc = typeof p.documentation === 'string' ? p.documentation : p.documentation.value;
                    }
                    return {
                      label: p.label,
                      documentation: pDoc ? { value: pDoc } : undefined
                    };
                  })
                };
              }),
              activeSignature: res.activeSignature ?? 0,
              activeParameter: res.activeParameter ?? 0
            },
            dispose: () => {}
          };
        } catch {
          return null;
        }
      }
    });

    // 3. Hover Provider (C++ Declarations & SFML 2.6.2 Docs)
    monaco.languages.registerHoverProvider('cpp', {
      provideHover: async (model: any, position: any) => {
        if (!this.isReady()) {
          return null;
        }

        const uri = this.getModelUri(model);
        const line = position.lineNumber - 1;
        const character = position.column - 1;

        try {
          const res = await this.sendRequest('textDocument/hover', {
            textDocument: { uri },
            position: { line, character }
          });

          if (!res || !res.contents) {
            return null;
          }

          let contents: any[] = [];
          if (Array.isArray(res.contents)) {
            contents = res.contents.map((c: any) => ({ value: c.value || c }));
          } else if (res.contents.value) {
            contents = [{ value: res.contents.value }];
          } else if (typeof res.contents === 'string') {
            contents = [{ value: res.contents }];
          }

          return { contents };
        } catch {
          return null;
        }
      }
    });

    // 4. Definition Provider (Go To Definition)
    monaco.languages.registerDefinitionProvider('cpp', {
      provideDefinition: async (model: any, position: any) => {
        if (!this.isReady()) {
          return null;
        }

        const uri = this.getModelUri(model);
        const line = position.lineNumber - 1;
        const character = position.column - 1;

        try {
          const res = await this.sendRequest('textDocument/definition', {
            textDocument: { uri },
            position: { line, character }
          });

          if (!res) return null;

          const locs = Array.isArray(res) ? res : [res];
          return locs.map((loc: any) => {
            const targetUri = loc.targetUri || loc.uri;
            const targetRange = loc.targetRange || loc.range;
            return {
              uri: monaco.Uri.parse(targetUri),
              range: {
                startLineNumber: targetRange.start.line + 1,
                startColumn: targetRange.start.character + 1,
                endLineNumber: targetRange.end.line + 1,
                endColumn: targetRange.end.character + 1
              }
            };
          });
        } catch {
          return null;
        }
      }
    });
  }

  /**
   * Tracks a Monaco model for a project file and synchronizes with clangd.
   */
  public registerModel(filePath: string, model: any): void {
    this.activeModels.set(filePath, model);
    const content = model.getValue();
    this.notifyDocumentOpen(filePath, content);

    // Listen for model content edits
    model.onDidChangeContent(() => {
      this.notifyDocumentChange(filePath, model.getValue());
    });
  }

  public notifyDocumentOpen(filePath: string, content: string): void {
    if (!this.isReady() || !this.currentProjectId) return;

    const uri = this.toUri(filePath);
    const version = 1;
    this.openDocuments.set(uri, version);

    this.sendNotification('textDocument/didOpen', {
      textDocument: {
        uri,
        languageId: 'cpp',
        version,
        text: content
      }
    });
  }

  public notifyDocumentChange(filePath: string, content: string): void {
    if (!this.isReady() || !this.currentProjectId) return;

    const uri = this.toUri(filePath);
    const currentVersion = this.openDocuments.get(uri) || 1;
    const newVersion = currentVersion + 1;
    this.openDocuments.set(uri, newVersion);

    this.sendNotification('textDocument/didChange', {
      textDocument: {
        uri,
        version: newVersion
      },
      contentChanges: [
        { text: content }
      ]
    });
  }

  public notifyDocumentClose(filePath: string): void {
    if (!this.isReady() || !this.currentProjectId) return;

    const uri = this.toUri(filePath);
    this.openDocuments.delete(uri);

    this.sendNotification('textDocument/didClose', {
      textDocument: { uri }
    });
  }

  private isReady(): boolean {
    return !!this.socket && this.socket.readyState === WebSocket.OPEN && this.isInitialized;
  }

  private toUri(filePath: string): string {
    const cleanPath = filePath.startsWith('/') ? filePath.substring(1) : filePath;
    return `file:///workspace/projects/${this.currentProjectId}/${cleanPath}`;
  }

  private getModelUri(model: any): string {
    // Check registered models
    for (const [path, m] of this.activeModels.entries()) {
      if (m === model) {
        return this.toUri(path);
      }
    }
    // Fallback based on model URI
    const uriStr = model.uri.toString();
    const fileName = uriStr.split('/').pop() || 'main.cpp';
    return this.toUri(fileName);
  }

  private sendInitialize(): void {
    if (!this.currentProjectId) return;

    const rootUri = `file:///workspace/projects/${this.currentProjectId}`;

    this.sendRequest('initialize', {
      processId: null,
      rootUri,
      capabilities: {
        textDocument: {
          synchronization: {
            willSave: false,
            didSave: true
          },
          completion: {
            completionItem: {
              snippetSupport: true,
              documentationFormat: ['markdown', 'plaintext']
            },
            contextSupport: true
          },
          hover: {
            contentFormat: ['markdown', 'plaintext']
          },
          signatureHelp: {
            signatureInformation: {
              documentationFormat: ['markdown', 'plaintext'],
              parameterInformation: {
                labelOffsetSupport: true
              }
            }
          },
          definition: {
            dynamicRegistration: false
          },
          publishDiagnostics: {
            relatedInformation: true
          }
        }
      }
    }).then(() => {
      this.isInitialized = true;
      this.sendNotification('initialized', {});
      console.log('[LSP] Language Server initialized for project', this.currentProjectId);

      // Open all active models
      for (const [path, model] of this.activeModels.entries()) {
        this.notifyDocumentOpen(path, model.getValue());
      }
    }).catch(err => {
      console.error('[LSP] Initialize failed:', err);
    });
  }

  private handleMessage(data: string): void {
    try {
      const msg = JSON.parse(data);

      // Handle Response to Request
      if (typeof msg.id === 'number' && this.pendingRequests.has(msg.id)) {
        const req = this.pendingRequests.get(msg.id)!;
        this.pendingRequests.delete(msg.id);

        if (msg.error) {
          req.reject(msg.error);
        } else {
          req.resolve(msg.result);
        }
        return;
      }

      // Handle Notifications from clangd
      if (msg.method === 'textDocument/publishDiagnostics') {
        this.handleDiagnostics(msg.params);
      }
    } catch (err) {
      console.warn('[LSP] Failed to parse message from server:', err, data);
    }
  }

  /**
   * Displays Visual Studio-style real-time red squiggly error and warning markers in Monaco.
   */
  private handleDiagnostics(params: { uri: string; diagnostics: any[] }): void {
    if (typeof monaco === 'undefined' || !params || !params.diagnostics) return;

    // Find the corresponding model
    let targetModel: any = null;
    for (const [path, model] of this.activeModels.entries()) {
      const modelUri = this.toUri(path);
      if (modelUri === params.uri || params.uri.endsWith('/' + path)) {
        targetModel = model;
        break;
      }
    }

    if (!targetModel) return;

    const markers = params.diagnostics.map(d => {
      let severity = monaco.MarkerSeverity.Error;
      if (d.severity === 2) severity = monaco.MarkerSeverity.Warning;
      if (d.severity === 3) severity = monaco.MarkerSeverity.Info;
      if (d.severity === 4) severity = monaco.MarkerSeverity.Hint;

      return {
        severity,
        message: d.message,
        source: 'clangd',
        startLineNumber: (d.range?.start?.line ?? 0) + 1,
        startColumn: (d.range?.start?.character ?? 0) + 1,
        endLineNumber: (d.range?.end?.line ?? 0) + 1,
        endColumn: (d.range?.end?.character ?? 0) + 1
      };
    });

    monaco.editor.setModelMarkers(targetModel, 'clangd', markers);
  }

  private sendRequest(method: string, params: any): Promise<any> {
    return new Promise((resolve, reject) => {
      if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
        return reject(new Error('WebSocket is not open'));
      }

      const id = this.messageId++;
      this.pendingRequests.set(id, { resolve, reject });

      const payload = JSON.stringify({
        jsonrpc: '2.0',
        id,
        method,
        params
      });

      this.socket.send(payload);

      // 10-second timeout for request
      setTimeout(() => {
        if (this.pendingRequests.has(id)) {
          this.pendingRequests.delete(id);
          reject(new Error(`LSP Request ${method} (id ${id}) timed out`));
        }
      }, 10000);
    });
  }

  private sendNotification(method: string, params: any): void {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) return;

    const payload = JSON.stringify({
      jsonrpc: '2.0',
      method,
      params
    });

    this.socket.send(payload);
  }
}
