import { Component, ViewChild, ElementRef, OnInit, OnDestroy, HostListener } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { Subscription } from 'rxjs';
import {
  ProjectService,
  UserDto,
  ProjectDetailDto,
  ProjectSummaryDto,
  ProjectFileDto,
  ProjectAssetDto,
} from './services/project.service';
import { SessionService, SessionResponse, DisplayInfo } from './services/session.service';
import { LspClientService } from './services/lsp-client.service';

declare const monaco: any;

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css',
})
export class AppComponent implements OnInit, OnDestroy {
  @ViewChild('editorContainer', { static: false })
  editorContainer?: ElementRef<HTMLDivElement>;
  @ViewChild('vncFrame', { static: false })
  vncFrame?: ElementRef<HTMLIFrameElement>;

  private editor: any;
  private fileModels = new Map<number, any>();
  private subs: Subscription[] = [];
  private saveTimeout: any = null;

  // ─── Resizable & Customizable Panel Dimensions & Visibility ────────────────
  sidebarWidth = 240;
  displayWidth = 480;
  terminalHeight = 160;

  sidebarVisible = true;
  assetsVisible = true;
  displayVisible = true;
  terminalVisible = true;
  terminalCollapsed = false;

  maximizedPanel: 'editor' | 'display' | 'terminal' | null = null;
  isResizing = false;
  activeResizePanel: 'sidebar' | 'display' | 'terminal' | null = null;
  private resizeStartX = 0;
  private resizeStartY = 0;
  private resizeStartWidth = 0;
  private resizeStartHeight = 0;

  // ─── Pop-out Separate Game Window ──────────────────────────────────────────
  poppedOut = false;
  private popOutWindow: Window | null = null;

  // State from ProjectService
  currentUser: UserDto | null = null;
  projects: ProjectSummaryDto[] = [];
  currentProject: ProjectDetailDto | null = null;
  activeFile: ProjectFileDto | null = null;
  saveStatus: 'saved' | 'saving' | 'unsaved' = 'saved';

  // State from SessionService
  session: SessionResponse | null = null;
  displayInfo: DisplayInfo | null = null;
  isLoading = false;
  isUploading = false;
  uploadError: string | null = null;
  isDraggingOver = false;
  terminalOutput = '';
  vncUrl: string | null = null;
  safeVncUrl: SafeResourceUrl | null = null;

  // Modals
  showUserModal = false;
  showProjectsModal = false;
  showNewProjectModal = false;
  showNewFileModal = false;

  usernameInput = '';
  newProjectName = '';
  newProjectTemplate = 'sprite';
  newFileName = '';
  fileError: string | null = null;

  templates = [
    {
      key: 'sprite',
      title: 'Texture & Sprite (Multi-File)',
      desc: 'Multi-file project (main.cpp, Player.hpp, Player.cpp) with spaceship sprite and keyboard movement',
      badge: 'Recommended',
    },
    {
      key: 'shapes',
      title: 'Shapes & Drawing',
      desc: 'Geometric primitives (circles, rectangles) with smooth rotation animations',
      badge: 'Beginner',
    },
    {
      key: 'ball',
      title: 'Bouncing Ball (Multi-File)',
      desc: 'Modular physics ball bouncing with boundary detection in Ball.hpp and Ball.cpp',
      badge: 'Game Math',
    },
    {
      key: 'empty',
      title: 'Empty SFML Project',
      desc: 'Minimal clean SFML window starter ready for custom code',
      badge: 'Clean Slate',
    },
  ];

  constructor(
    public projectService: ProjectService,
    public sessionService: SessionService,
    private lspClient: LspClientService,
    private sanitizer: DomSanitizer
  ) {}

  ngOnInit(): void {
    this.loadLayoutState();
    this.sessionService.initSession().subscribe();

    this.subs.push(
      this.projectService.currentUser$.subscribe((u) => {
        this.currentUser = u;
        if (!u) {
          this.showUserModal = true;
        } else {
          this.showUserModal = false;
        }
      }),

      this.projectService.projects$.subscribe((p) => {
        this.projects = p;
      }),

      this.projectService.currentProject$.subscribe((p) => {
        const prevId = this.currentProject?.id;
        this.currentProject = p;
        if (p && p.id !== prevId) {
          this.fileModels.clear();
          this.lspClient.connect(p.id);
          // Stop any active runner when switching projects
          if (this.isRunning) {
            this.stop();
          }
        }
      }),

      this.projectService.activeFile$.subscribe((f) => {
        this.activeFile = f;
        if (f && this.editor) {
          this.switchToFileModel(f);
        }
      }),

      this.projectService.saveStatus$.subscribe((s) => {
        this.saveStatus = s;
      }),

      this.sessionService.session$.subscribe((s) => {
        this.session = s;
        this.updateTerminalOutput();
      }),

      this.sessionService.displayInfo$.subscribe((d) => {
        this.displayInfo = d;
        if (d) {
          this.vncUrl = d.url || `http://${d.host}:${d.port}/vnc_lite.html?scale=true`;
          this.safeVncUrl = this.sanitizer.bypassSecurityTrustResourceUrl(this.vncUrl);
        } else {
          this.vncUrl = null;
          this.safeVncUrl = null;
        }
      }),

      this.sessionService.isLoading$.subscribe((l) => (this.isLoading = l))
    );

    this.initMonaco();
  }

  ngOnDestroy(): void {
    this.subs.forEach((s) => s.unsubscribe());
    if (this.saveTimeout) clearTimeout(this.saveTimeout);
    this.lspClient.disconnect();
    this.editor?.dispose();
    this.fileModels.clear();
  }

  @HostListener('window:resize')
  onWindowResize(): void {
    this.editor?.layout();
  }

  // ─── Resizing Splitters ───────────────────────────────────────────────────

  startResize(panel: 'sidebar' | 'display' | 'terminal', event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();

    this.isResizing = true;
    this.activeResizePanel = panel;
    this.resizeStartX = event.clientX;
    this.resizeStartY = event.clientY;

    if (panel === 'sidebar') {
      this.resizeStartWidth = this.sidebarWidth;
    } else if (panel === 'display') {
      this.resizeStartWidth = this.displayWidth;
    } else if (panel === 'terminal') {
      this.resizeStartHeight = this.terminalHeight;
    }

    const onMouseMove = (e: MouseEvent) => {
      if (!this.isResizing) return;

      if (this.activeResizePanel === 'sidebar') {
        const deltaX = e.clientX - this.resizeStartX;
        this.sidebarWidth = Math.max(160, Math.min(600, this.resizeStartWidth + deltaX));
      } else if (this.activeResizePanel === 'display') {
        const deltaX = this.resizeStartX - e.clientX;
        this.displayWidth = Math.max(260, Math.min(900, this.resizeStartWidth + deltaX));
      } else if (this.activeResizePanel === 'terminal') {
        const deltaY = this.resizeStartY - e.clientY;
        this.terminalHeight = Math.max(40, Math.min(600, this.resizeStartHeight + deltaY));
      }

      this.editor?.layout();
    };

    const onMouseUp = () => {
      this.isResizing = false;
      this.activeResizePanel = null;
      window.removeEventListener('mousemove', onMouseMove);
      window.removeEventListener('mouseup', onMouseUp);
      this.saveLayoutState();
      this.editor?.layout();
    };

    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', onMouseUp);
  }

  // ─── Panel Toggles & Maximize ─────────────────────────────────────────────

  toggleSidebar(): void {
    this.sidebarVisible = !this.sidebarVisible;
    this.saveLayoutState();
    setTimeout(() => this.editor?.layout(), 50);
  }

  toggleAssets(): void {
    this.assetsVisible = !this.assetsVisible;
    this.saveLayoutState();
  }

  toggleDisplay(): void {
    this.displayVisible = !this.displayVisible;
    this.saveLayoutState();
    setTimeout(() => this.editor?.layout(), 50);
  }

  toggleTerminal(): void {
    this.terminalVisible = !this.terminalVisible;
    this.saveLayoutState();
    setTimeout(() => this.editor?.layout(), 50);
  }

  toggleTerminalCollapse(): void {
    this.terminalCollapsed = !this.terminalCollapsed;
    this.saveLayoutState();
    setTimeout(() => this.editor?.layout(), 50);
  }

  toggleMaximize(panel: 'editor' | 'display' | 'terminal'): void {
    if (this.maximizedPanel === panel) {
      this.maximizedPanel = null;
    } else {
      this.maximizedPanel = panel;
    }
    setTimeout(() => this.editor?.layout(), 50);
  }

  // ─── Pop-Out Game Window in Separate Browser Tab ──────────────────────────

  popOutGameWindow(): void {
    if (!this.vncUrl) {
      if (this.currentProject && !this.isRunning) {
        this.run();
      }
    }
    this.poppedOut = true;
    const url = this.vncUrl || 'about:blank';
    const popout = window.open(
      url,
      'SFML_Game_Window',
      'width=1024,height=768,menubar=no,toolbar=no,location=no,status=no,resizable=yes'
    );
    this.popOutWindow = popout;

    if (popout) {
      const checkTimer = setInterval(() => {
        if (popout.closed) {
          clearInterval(checkTimer);
          this.poppedOut = false;
          this.popOutWindow = null;
        }
      }, 1000);
    }
  }

  focusPopOut(): void {
    if (this.popOutWindow && !this.popOutWindow.closed) {
      this.popOutWindow.focus();
    } else {
      this.popOutGameWindow();
    }
  }

  dockBack(): void {
    if (this.popOutWindow && !this.popOutWindow.closed) {
      this.popOutWindow.close();
    }
    this.poppedOut = false;
    this.popOutWindow = null;
    this.displayVisible = true;
    setTimeout(() => this.editor?.layout(), 50);
  }

  // ─── Layout Persistence ───────────────────────────────────────────────────

  loadLayoutState(): void {
    try {
      const saved = localStorage.getItem('sfml_ide_layout');
      if (saved) {
        const s = JSON.parse(saved);
        if (s.sidebarWidth) this.sidebarWidth = s.sidebarWidth;
        if (s.displayWidth) this.displayWidth = s.displayWidth;
        if (s.terminalHeight) this.terminalHeight = s.terminalHeight;
        if (typeof s.sidebarVisible === 'boolean') this.sidebarVisible = s.sidebarVisible;
        if (typeof s.assetsVisible === 'boolean') this.assetsVisible = s.assetsVisible;
        if (typeof s.displayVisible === 'boolean') this.displayVisible = s.displayVisible;
        if (typeof s.terminalVisible === 'boolean') this.terminalVisible = s.terminalVisible;
        if (typeof s.terminalCollapsed === 'boolean') this.terminalCollapsed = s.terminalCollapsed;
      }
    } catch {
      /* ignore storage errors */
    }
  }

  saveLayoutState(): void {
    try {
      localStorage.setItem(
        'sfml_ide_layout',
        JSON.stringify({
          sidebarWidth: this.sidebarWidth,
          displayWidth: this.displayWidth,
          terminalHeight: this.terminalHeight,
          sidebarVisible: this.sidebarVisible,
          assetsVisible: this.assetsVisible,
          displayVisible: this.displayVisible,
          terminalVisible: this.terminalVisible,
          terminalCollapsed: this.terminalCollapsed,
        })
      );
    } catch {
      /* ignore storage errors */
    }
  }

  // ─── Monaco Editor Setup ──────────────────────────────────────────────────

  private initMonaco(): void {
    if (typeof (window as any).monaco !== 'undefined') {
      this.createEditor();
      return;
    }

    const script = document.createElement('script');
    script.src = 'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.45.0/min/vs/loader.min.js';
    script.onload = () => {
      (window as any).require.config({
        paths: {
          vs: 'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.45.0/min/vs',
        },
      });
      (window as any).require(['vs/editor/editor.main'], () => {
        this.createEditor();
      });
    };
    document.head.appendChild(script);
  }

  private createEditor(): void {
    if (!this.editorContainer?.nativeElement) {
      setTimeout(() => this.createEditor(), 50);
      return;
    }

    monaco.editor.defineTheme('sfml-ide-dark', {
      base: 'vs-dark',
      inherit: true,
      rules: [
        { token: 'comment', foreground: '7d8590', fontStyle: 'italic' },
        { token: 'keyword', foreground: 'ff7b72' },
        { token: 'string', foreground: 'a5d6ff' },
        { token: 'number', foreground: '79c0ff' },
        { token: 'type', foreground: 'ffa657' },
        { token: 'function', foreground: 'd2a8ff' },
        { token: 'variable', foreground: 'ffa657' },
      ],
      colors: {
        'editor.background': '#0d1117',
        'editor.foreground': '#e6edf3',
        'editor.lineHighlightBackground': '#161b2280',
        'editorCursor.foreground': '#58a6ff',
        'editor.selectionBackground': '#264f7888',
        'editorLineNumber.foreground': '#6e7681',
        'editorLineNumber.activeForeground': '#e6edf3',
        'editorIndentGuide.background1': '#21262d',
        'editorBracketMatch.background': '#58a6ff22',
        'editorBracketMatch.border': '#58a6ff66',
      },
    });

    this.editor = monaco.editor.create(this.editorContainer.nativeElement, {
      theme: 'sfml-ide-dark',
      fontSize: 14,
      fontFamily: "'JetBrains Mono', 'Fira Code', Consolas, monospace",
      fontLigatures: true,
      lineNumbers: 'on',
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      automaticLayout: true,
      bracketPairColorization: { enabled: true },
      guides: { bracketPairs: true, indentation: true },
      padding: { top: 14, bottom: 14 },
      renderLineHighlight: 'all',
      smoothScrolling: true,
      cursorBlinking: 'smooth',
      cursorSmoothCaretAnimation: 'on',
      suggestOnTriggerCharacters: true,
      quickSuggestions: { other: true, comments: false, strings: true },
      acceptSuggestionOnEnter: 'on',
      tabCompletion: 'on',
      wordBasedSuggestions: 'off',
      parameterHints: { enabled: true, cycle: true },
      tabSize: 4,
      wordWrap: 'off',
      overviewRulerBorder: false,
      hideCursorInOverviewRuler: true,
      contextmenu: true,
    });

    this.lspClient.registerMonacoProviders();

    // Run shortcut Ctrl+Enter
    this.editor.addAction({
      id: 'run-code',
      label: 'Run SFML Project',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter],
      run: () => this.run(),
    });

    // Ctrl+S -> save immediately
    this.editor.addAction({
      id: 'save-code',
      label: 'Save File',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS],
      run: () => this.saveCurrentFileNow(),
    });

    if (this.activeFile) {
      this.switchToFileModel(this.activeFile);
    }
  }

  private switchToFileModel(file: ProjectFileDto): void {
    if (!this.editor) return;

    let model = this.fileModels.get(file.id);
    if (!model) {
      const uri = monaco.Uri.parse(`file:///${file.path}`);
      const existing = monaco.editor.getModel(uri);
      if (existing) existing.dispose();

      model = monaco.editor.createModel(file.content, 'cpp', uri);
      this.lspClient.registerModel(file.path, model);

      model.onDidChangeContent(() => {
        if (this.activeFile?.id === file.id) {
          this.saveStatus = 'unsaved';
          if (this.saveTimeout) clearTimeout(this.saveTimeout);
          this.saveTimeout = setTimeout(() => {
            this.saveCurrentFileNow();
          }, 800);
        }
      });

      this.fileModels.set(file.id, model);
    }

    this.editor.setModel(model);
  }

  private saveCurrentFileNow(): void {
    if (!this.currentProject || !this.activeFile || !this.editor) return;
    if (this.saveTimeout) clearTimeout(this.saveTimeout);

    const content = this.editor.getValue();
    this.saveStatus = 'saving';
    this.projectService.updateFile(this.currentProject.id, this.activeFile.id, content).subscribe({
      next: () => {
        this.saveStatus = 'saved';
      },
      error: (err) => {
        console.error('Failed to save file:', err);
        this.saveStatus = 'unsaved';
      },
    });
  }

  // ─── File Navigation & Tabs ───────────────────────────────────────────────

  selectFile(file: ProjectFileDto): void {
    if (this.activeFile?.id === file.id) return;
    this.saveCurrentFileNow();
    this.projectService.setActiveFile(file);
  }

  openNewFileModal(): void {
    this.newFileName = '';
    this.fileError = null;
    this.showNewFileModal = true;
  }

  confirmCreateFile(): void {
    if (!this.currentProject) return;
    const name = this.newFileName.trim();
    if (!name) {
      this.fileError = 'Filename cannot be empty.';
      return;
    }
    const ext = name.split('.').pop()?.toLowerCase();
    if (ext !== 'cpp' && ext !== 'hpp' && ext !== 'h') {
      this.fileError = 'File must have .cpp, .hpp, or .h extension.';
      return;
    }
    if (this.currentProject.files.some((f) => f.path.toLowerCase() === name.toLowerCase())) {
      this.fileError = `A file named '${name}' already exists in this project.`;
      return;
    }

    const starter = ext === 'hpp' || ext === 'h' ? '#pragma once\n\n' : '#include <SFML/Graphics.hpp>\n\n';

    this.projectService.createFile(this.currentProject.id, name, starter).subscribe({
      next: () => {
        this.showNewFileModal = false;
        this.newFileName = '';
      },
      error: (err) => {
        this.fileError = err.error?.error || 'Failed to create file.';
      },
    });
  }

  confirmDeleteFile(file: ProjectFileDto, event: MouseEvent): void {
    event.stopPropagation();
    if (!this.currentProject) return;
    if (file.path.toLowerCase() === 'main.cpp') {
      alert('main.cpp is the primary entry point and cannot be deleted.');
      return;
    }
    if (confirm(`Delete ${file.path}?`)) {
      this.fileModels.get(file.id)?.dispose();
      this.fileModels.delete(file.id);
      this.projectService.deleteFile(this.currentProject.id, file.id).subscribe();
    }
  }

  // ─── Project Management ───────────────────────────────────────────────────

  openProjectsModal(): void {
    if (this.currentUser) {
      this.projectService.loadProjects(this.currentUser.id).subscribe();
    }
    this.showProjectsModal = true;
  }

  openNewProjectModal(): void {
    this.newProjectName = '';
    this.newProjectTemplate = 'sprite';
    this.showProjectsModal = false;
    this.showNewProjectModal = true;
  }

  confirmCreateProject(): void {
    if (!this.currentUser) return;
    const name = this.newProjectName.trim() || 'My SFML Game';
    this.projectService.createProject(this.currentUser.id, name, this.newProjectTemplate).subscribe({
      next: () => {
        this.showNewProjectModal = false;
      },
      error: (err) => {
        alert(err.error?.error || 'Failed to create project.');
      },
    });
  }

  switchProject(p: ProjectSummaryDto): void {
    if (this.currentProject?.id === p.id) {
      this.showProjectsModal = false;
      return;
    }
    this.saveCurrentFileNow();
    this.projectService.loadProject(p.id).subscribe({
      next: () => {
        this.showProjectsModal = false;
      },
    });
  }

  deleteProject(p: ProjectSummaryDto, event: MouseEvent): void {
    event.stopPropagation();
    if (confirm(`Delete project '${p.name}'? All files and assets will be permanently removed.`)) {
      this.projectService.deleteProject(p.id).subscribe();
    }
  }

  // ─── User Profile ─────────────────────────────────────────────────────────

  confirmLogin(): void {
    const username = this.usernameInput.trim();
    if (!username) return;
    this.projectService.login(username).subscribe({
      next: () => {
        this.showUserModal = false;
        this.usernameInput = '';
      },
      error: (err) => {
        alert(err.error?.error || 'Login failed.');
      },
    });
  }

  changeUser(): void {
    this.usernameInput = this.currentUser?.username || '';
    this.showUserModal = true;
  }

  // ─── Execution ────────────────────────────────────────────────────────────

  run(): void {
    if (this.isLoading || !this.currentProject) return;
    this.saveCurrentFileNow();
    this.terminalOutput = '';
    this.sessionService.runProject(this.currentProject.id);

    // If display is hidden, make it visible on run so student sees output
    if (!this.displayVisible && !this.poppedOut) {
      this.displayVisible = true;
      setTimeout(() => this.editor?.layout(), 50);
    }
  }

  stop(): void {
    this.sessionService.stopSession();
  }

  focusIframe(): void {
    if (this.vncFrame?.nativeElement) {
      this.vncFrame.nativeElement.focus();
      try {
        this.vncFrame.nativeElement.contentWindow?.focus();
      } catch {
        /* safely ignored */
      }
    }
  }

  private updateTerminalOutput(): void {
    if (!this.session) {
      this.terminalOutput = '';
      return;
    }

    const lines: string[] = [];
    switch (this.session.status) {
      case 'Starting':
        lines.push('⏳ Starting isolated Docker sandbox...');
        break;
      case 'Compiling':
        lines.push('🔨 Compiling all project C++ sources with G++ 11...');
        break;
      case 'CompileError':
        lines.push('❌ Compilation Failed:\n');
        if (this.session.compilerOutput) lines.push(this.session.compilerOutput);
        break;
      case 'Running':
        lines.push('✅ Multi-file C++ build succeeded');
        lines.push('🖥️  SFML 2.6.2 window active via Xvfb + noVNC');
        if (this.poppedOut && this.popOutWindow && this.vncUrl) {
          // If popped out, navigate the popup window to the fresh vncUrl
          try {
            if (this.popOutWindow.location.href !== this.vncUrl) {
              this.popOutWindow.location.href = this.vncUrl;
            }
          } catch { }
        }
        break;
      case 'Stopped':
        lines.push('⏹️  Execution session stopped');
        break;
      case 'TimedOut':
        lines.push('⏱️  Execution timed out');
        break;
      case 'Error':
        lines.push('❌ Execution Error');
        if (this.session.errorMessage) lines.push('\n' + this.session.errorMessage);
        if (this.session.compilerOutput) lines.push('\n' + this.session.compilerOutput);
        break;
    }
    this.terminalOutput = lines.join('\n');
  }

  get isRunning(): boolean {
    return this.session?.status === 'Running';
  }

  get statusText(): string {
    if (this.isLoading) return 'Loading...';
    if (!this.session) return 'Ready';
    switch (this.session.status) {
      case 'Starting':
        return 'Starting...';
      case 'Compiling':
        return 'Compiling...';
      case 'Running':
        return 'Running';
      case 'CompileError':
        return 'Compile Error';
      case 'Stopped':
        return 'Stopped';
      case 'TimedOut':
        return 'Timed Out';
      case 'Error':
        return 'Error';
      default:
        return 'Ready';
    }
  }

  get statusClass(): string {
    if (!this.session) return 'ready';
    switch (this.session.status) {
      case 'Running':
        return 'running';
      case 'CompileError':
      case 'Error':
        return 'error';
      case 'Starting':
      case 'Compiling':
        return 'loading';
      default:
        return 'ready';
    }
  }

  // ─── Assets ───────────────────────────────────────────────────────────────

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (!input.files || input.files.length === 0 || !this.currentProject) return;
    this.uploadFiles(Array.from(input.files));
    input.value = '';
  }

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDraggingOver = true;
  }

  onDragLeave(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDraggingOver = false;
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDraggingOver = false;
    if (event.dataTransfer?.files && event.dataTransfer.files.length > 0 && this.currentProject) {
      this.uploadFiles(Array.from(event.dataTransfer.files));
    }
  }

  private uploadFiles(files: File[]): void {
    if (!this.currentProject) return;
    this.uploadError = null;
    this.isUploading = true;

    let uploadedCount = 0;
    files.forEach((file) => {
      this.projectService.uploadAsset(this.currentProject!.id, file).subscribe({
        next: () => {
          uploadedCount++;
          if (uploadedCount === files.length) {
            this.isUploading = false;
          }
        },
        error: (err) => {
          this.isUploading = false;
          this.uploadError = err.error?.error || `Failed to upload ${file.name}`;
        },
      });
    });
  }

  deleteAsset(asset: ProjectAssetDto, event: MouseEvent): void {
    event.stopPropagation();
    if (!this.currentProject) return;
    if (confirm(`Delete asset '${asset.fileName}'?`)) {
      this.projectService.deleteAsset(this.currentProject.id, asset.id).subscribe({
        error: (err) => {
          this.uploadError = err.error?.error || `Failed to delete ${asset.fileName}`;
        },
      });
    }
  }

  formatBytes(bytes: number): string {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
  }

  isImage(name: string): boolean {
    return /\.(png|jpe?g|bmp|tga|psd)$/i.test(name);
  }

  isAudio(name: string): boolean {
    return /\.(wav|ogg|flac)$/i.test(name);
  }

  isFont(name: string): boolean {
    return /\.(ttf|otf)$/i.test(name);
  }
}
