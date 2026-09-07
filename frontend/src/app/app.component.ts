import { Component, ViewChild, ElementRef, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { SessionService, SessionResponse, DisplayInfo } from './services/session.service';
import { Subscription } from 'rxjs';

declare const monaco: any;

const DEFAULT_CODE = `#include <SFML/Graphics.hpp>

int main()
{
    // Create an interactive SFML window
    // Features: draggable title bar to slide, border dragging to resize,
    // [X] to close, [□] to maximize, and keyboard/mouse interaction!
    sf::RenderWindow window(sf::VideoMode(640, 480), "SFML Playground");
    window.setFramerateLimit(60);

    // Create an interactive player circle
    sf::CircleShape player(40.f);
    player.setFillColor(sf::Color(88, 166, 255));
    player.setOutlineThickness(3.f);
    player.setOutlineColor(sf::Color::White);
    player.setOrigin(40.f, 40.f);
    player.setPosition(320.f, 240.f);

    while (window.isOpen())
    {
        sf::Event event;
        while (window.pollEvent(event))
        {
            // Close window when [X] icon is clicked or Alt+F4
            if (event.type == sf::Event::Closed)
                window.close();

            // Adjust viewport when window is resized (bigger / smaller)
            if (event.type == sf::Event::Resized)
            {
                sf::FloatRect visibleArea(0, 0, (float)event.size.width, (float)event.size.height);
                window.setView(sf::View(visibleArea));
            }

            // Mouse interaction: click anywhere to jump player there
            if (event.type == sf::Event::MouseButtonPressed)
            {
                player.setPosition((float)event.mouseButton.x, (float)event.mouseButton.y);
                player.setFillColor(sf::Color(255, 123, 114)); // Change color on click
            }
            if (event.type == sf::Event::MouseButtonReleased)
            {
                player.setFillColor(sf::Color(88, 166, 255));
            }
        }

        // Keyboard interaction: Arrow keys or WASD to move player
        float speed = 4.0f;
        if (sf::Keyboard::isKeyPressed(sf::Keyboard::Left) || sf::Keyboard::isKeyPressed(sf::Keyboard::A))
            player.move(-speed, 0.f);
        if (sf::Keyboard::isKeyPressed(sf::Keyboard::Right) || sf::Keyboard::isKeyPressed(sf::Keyboard::D))
            player.move(speed, 0.f);
        if (sf::Keyboard::isKeyPressed(sf::Keyboard::Up) || sf::Keyboard::isKeyPressed(sf::Keyboard::W))
            player.move(0.f, -speed);
        if (sf::Keyboard::isKeyPressed(sf::Keyboard::Down) || sf::Keyboard::isKeyPressed(sf::Keyboard::S))
            player.move(0.f, speed);

        window.clear(sf::Color(22, 27, 34));
        window.draw(player);
        window.display();
    }

    return 0;
}
`;

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css',
})
export class AppComponent implements OnInit, OnDestroy {
  @ViewChild('editorContainer', { static: true })
  editorContainer!: ElementRef<HTMLDivElement>;
  @ViewChild('vncFrame', { static: false })
  vncFrame?: ElementRef<HTMLIFrameElement>;

  private editor: any;
  private subs: Subscription[] = [];

  session: SessionResponse | null = null;
  displayInfo: DisplayInfo | null = null;
  isLoading = false;
  terminalOutput = '';
  vncUrl: string | null = null;
  safeVncUrl: SafeResourceUrl | null = null;

  constructor(
    public sessionService: SessionService,
    private sanitizer: DomSanitizer
  ) {}

  ngOnInit(): void {
    this.initMonaco();

    this.subs.push(
      this.sessionService.session$.subscribe((s) => {
        this.session = s;
        this.updateTerminalOutput();
      }),
      this.sessionService.displayInfo$.subscribe((d) => {
        this.displayInfo = d;
        if (d) {
          // Build noVNC URL: connect to websockify using minimal lite client
          this.vncUrl = `http://${d.host}:${d.port}/vnc_lite.html?scale=true`;
          this.safeVncUrl = this.sanitizer.bypassSecurityTrustResourceUrl(this.vncUrl);
        } else {
          this.vncUrl = null;
          this.safeVncUrl = null;
        }
      }),
      this.sessionService.isLoading$.subscribe((l) => (this.isLoading = l))
    );
  }

  ngOnDestroy(): void {
    this.subs.forEach((s) => s.unsubscribe());
    this.editor?.dispose();
  }

  private initMonaco(): void {
    // Check if Monaco is already loaded
    if (typeof (window as any).monaco !== 'undefined') {
      this.createEditor();
      return;
    }

    // Load Monaco from CDN
    const script = document.createElement('script');
    script.src =
      'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.45.0/min/vs/loader.min.js';
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
    // Define custom dark theme
    monaco.editor.defineTheme('sfml-dark', {
      base: 'vs-dark',
      inherit: true,
      rules: [
        { token: 'comment', foreground: '6e7681', fontStyle: 'italic' },
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
        'editor.lineHighlightBackground': '#161b2299',
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
      value: DEFAULT_CODE,
      language: 'cpp',
      theme: 'sfml-dark',
      fontSize: 14,
      fontFamily: "'JetBrains Mono', 'Fira Code', Consolas, monospace",
      fontLigatures: true,
      lineNumbers: 'on',
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      automaticLayout: true,
      bracketPairColorization: { enabled: true },
      guides: {
        bracketPairs: true,
        indentation: true,
      },
      padding: { top: 16, bottom: 16 },
      renderLineHighlight: 'all',
      smoothScrolling: true,
      cursorBlinking: 'smooth',
      cursorSmoothCaretAnimation: 'on',
      suggestOnTriggerCharacters: true,
      tabSize: 4,
      wordWrap: 'off',
      overviewRulerBorder: false,
      hideCursorInOverviewRuler: true,
      renderWhitespace: 'none',
      contextmenu: true,
    });

    // Ctrl+Enter → Run
    this.editor.addAction({
      id: 'run-code',
      label: 'Run Code',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter],
      run: () => this.run(),
    });

    // Ctrl+S → prevent browser save
    this.editor.addAction({
      id: 'save-prevent',
      label: 'Save (disabled)',
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS],
      run: () => {
        /* no-op */
      },
    });
  }

  run(): void {
    if (this.isLoading || this.isRunning) return;
    const code = this.editor?.getValue() || '';
    if (!code.trim()) return;
    this.terminalOutput = '';
    this.sessionService.createSession(code);
  }

  stop(): void {
    this.sessionService.stopSession();
    this.vncUrl = null;
    this.safeVncUrl = null;
  }

  focusIframe(): void {
    if (this.vncFrame?.nativeElement) {
      this.vncFrame.nativeElement.focus();
      try {
        this.vncFrame.nativeElement.contentWindow?.focus();
      } catch {
        /* cross-origin safely ignored */
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
        lines.push('⏳ Starting execution environment...');
        break;
      case 'Compiling':
        lines.push('🔨 Compiling...');
        break;
      case 'CompileError':
        lines.push('❌ Compilation failed\n');
        if (this.session.compilerOutput)
          lines.push(this.session.compilerOutput);
        break;
      case 'Running':
        lines.push('✅ Compilation successful');
        lines.push('🖥️  Application is running');
        break;
      case 'Stopped':
        lines.push('⏹️  Application stopped');
        break;
      case 'TimedOut':
        lines.push('⏱️  Execution timed out');
        break;
      case 'Error':
        lines.push('❌ Error');
        if (this.session.errorMessage)
          lines.push('\n' + this.session.errorMessage);
        if (this.session.compilerOutput)
          lines.push('\n' + this.session.compilerOutput);
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
}
