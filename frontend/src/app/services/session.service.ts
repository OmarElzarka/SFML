import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, BehaviorSubject, interval, Subscription, timer, of } from 'rxjs';
import { switchMap, takeWhile, tap, catchError, map } from 'rxjs/operators';

export interface CreateSessionRequest {
  sessionId?: string;
  sourceCode: string;
}

export interface AssetInfo {
  name: string;
  size: number;
  uploadedAt?: string;
}

export interface SessionResponse {
  sessionId: string;
  status: SessionStatus;
  displayUrl?: string;
  compilerOutput?: string;
  errorMessage?: string;
  createdAt: string;
  assets?: AssetInfo[];
}

export interface DisplayInfo {
  host: string;
  port: number;
  path: string;
  url?: string;
}

export type SessionStatus =
  | 'Ready'
  | 'Starting'
  | 'Compiling'
  | 'CompileError'
  | 'Running'
  | 'Stopping'
  | 'Stopped'
  | 'TimedOut'
  | 'Error';

@Injectable({
  providedIn: 'root',
})
export class SessionService {
  private apiUrl = '/api';
  private heartbeatSub?: Subscription;

  private _session = new BehaviorSubject<SessionResponse | null>(null);
  private _displayInfo = new BehaviorSubject<DisplayInfo | null>(null);
  private _isLoading = new BehaviorSubject<boolean>(false);
  private _assets = new BehaviorSubject<AssetInfo[]>([]);

  session$ = this._session.asObservable();
  displayInfo$ = this._displayInfo.asObservable();
  isLoading$ = this._isLoading.asObservable();
  assets$ = this._assets.asObservable();

  constructor(private http: HttpClient) {}

  initSession(): Observable<SessionResponse> {
    return this.http.post<SessionResponse>(`${this.apiUrl}/sessions/init`, {}).pipe(
      tap((res) => {
        this._session.next(res);
        if (res.assets) {
          this._assets.next(res.assets);
        }
      })
    );
  }

  getOrCreateSessionId(): Observable<string> {
    const current = this._session.value;
    if (current && current.sessionId) {
      return of(current.sessionId);
    }
    return this.initSession().pipe(map((res) => res.sessionId));
  }

  uploadAsset(file: File): Observable<AssetInfo> {
    return this.getOrCreateSessionId().pipe(
      switchMap((sessionId) => {
        const formData = new FormData();
        formData.append('file', file, file.name);

        return this.http.post<AssetInfo>(
          `${this.apiUrl}/sessions/${sessionId}/assets`,
          formData
        ).pipe(
          tap((uploaded) => {
            const currentAssets = this._assets.value;
            const idx = currentAssets.findIndex(
              (a) => a.name.toLowerCase() === uploaded.name.toLowerCase()
            );
            if (idx >= 0) {
              const updated = [...currentAssets];
              updated[idx] = uploaded;
              this._assets.next(updated);
            } else {
              this._assets.next([...currentAssets, uploaded]);
            }
          })
        );
      })
    );
  }

  deleteAsset(assetName: string): Observable<void> {
    const session = this._session.value;
    if (!session?.sessionId) {
      return of(undefined);
    }

    return this.http
      .delete<void>(`${this.apiUrl}/sessions/${session.sessionId}/assets/${encodeURIComponent(assetName)}`)
      .pipe(
        tap(() => {
          this._assets.next(
            this._assets.value.filter(
              (a) => a.name.toLowerCase() !== assetName.toLowerCase()
            )
          );
        })
      );
  }

  createSession(sourceCode: string): void {
    this._isLoading.next(true);
    this._displayInfo.next(null);

    const currentSessionId = this._session.value?.sessionId;
    const body: CreateSessionRequest = {
      sessionId: currentSessionId || undefined,
      sourceCode,
    };

    this.http
      .post<SessionResponse>(`${this.apiUrl}/sessions`, body)
      .subscribe({
        next: (response) => {
          this._session.next(response);
          this._isLoading.next(false);
          if (response.assets) {
            this._assets.next(response.assets);
          }

          if (response.status === 'Running') {
            this.fetchDisplayInfo(response.sessionId);
            this.startHeartbeat(response.sessionId);
          } else if (
            response.status === 'Compiling' ||
            response.status === 'Starting'
          ) {
            this.pollUntilReady(response.sessionId);
          }
        },
        error: (err) => {
          this._isLoading.next(false);
          this._session.next({
            sessionId: currentSessionId || '',
            status: 'Error',
            errorMessage:
              err.error?.error || 'Failed to connect to the server.',
            createdAt: new Date().toISOString(),
          });
        },
      });
  }

  runProject(projectId: number): void {
    this._isLoading.next(true);
    this._displayInfo.next(null);

    const currentSessionId = this._session.value?.sessionId;
    this.http
      .post<SessionResponse>(`${this.apiUrl}/projects/${projectId}/run`, {
        sessionId: currentSessionId || undefined,
      })
      .subscribe({
        next: (response) => {
          this._session.next(response);
          this._isLoading.next(false);
          if (response.assets) {
            this._assets.next(response.assets);
          }

          if (response.status === 'Running') {
            this.fetchDisplayInfo(response.sessionId);
            this.startHeartbeat(response.sessionId);
          } else if (
            response.status === 'Compiling' ||
            response.status === 'Starting'
          ) {
            this.pollUntilReady(response.sessionId);
          }
        },
        error: (err) => {
          this._isLoading.next(false);
          this._session.next({
            sessionId: currentSessionId || '',
            status: 'Error',
            errorMessage:
              err.error?.error || 'Failed to connect to the server.',
            createdAt: new Date().toISOString(),
          });
        },
      });
  }

  private pollUntilReady(sessionId: string): void {
    const poll = interval(1000)
      .pipe(
        switchMap(() =>
          this.http.get<SessionResponse>(
            `${this.apiUrl}/sessions/${sessionId}`
          )
        ),
        tap((response) => {
          this._session.next(response);
          if (response.assets) {
            this._assets.next(response.assets);
          }
          if (response.status === 'Running') {
            this._isLoading.next(false);
            this.fetchDisplayInfo(sessionId);
            this.startHeartbeat(sessionId);
          }
        }),
        takeWhile(
          (response) =>
            response.status === 'Compiling' ||
            response.status === 'Starting',
          true
        ),
        catchError((err) => {
          this._isLoading.next(false);
          this._session.next({
            sessionId: sessionId,
            status: 'Error',
            errorMessage: 'Lost connection to the server.',
            createdAt: new Date().toISOString(),
          });
          return [];
        })
      )
      .subscribe();
  }

  private fetchDisplayInfo(sessionId: string): void {
    this.http
      .get<DisplayInfo>(`${this.apiUrl}/sessions/${sessionId}/display`)
      .subscribe({
        next: (info) => {
          this._displayInfo.next(info);
        },
        error: (err) => {
          console.error('Failed to get display info:', err);
          timer(1000).subscribe(() => this.fetchDisplayInfo(sessionId));
        },
      });
  }

  private startHeartbeat(sessionId: string): void {
    this.stopHeartbeat();
    this.heartbeatSub = interval(15000).subscribe(() => {
      this.http
        .post(`${this.apiUrl}/sessions/${sessionId}/heartbeat`, {})
        .subscribe({
          error: () => {
            this.stopHeartbeat();
          },
        });
    });
  }

  private stopHeartbeat(): void {
    this.heartbeatSub?.unsubscribe();
    this.heartbeatSub = undefined;
  }

  stopSession(): void {
    const session = this._session.value;
    if (!session?.sessionId) return;

    this.stopHeartbeat();
    this._isLoading.next(true);

    this.http
      .post(`${this.apiUrl}/sessions/${session.sessionId}/stop`, {})
      .subscribe({
        next: () => {
          this._session.next({
            ...session,
            status: 'Stopped',
          });
          this._displayInfo.next(null);
          this._isLoading.next(false);
        },
        error: () => {
          this._session.next({
            ...session,
            status: 'Stopped',
          });
          this._displayInfo.next(null);
          this._isLoading.next(false);
        },
      });
  }

  get currentSession(): SessionResponse | null {
    return this._session.value;
  }

  get isRunning(): boolean {
    return this._session.value?.status === 'Running';
  }

  reset(): void {
    this.stopHeartbeat();
    this._session.next(null);
    this._displayInfo.next(null);
    this._isLoading.next(false);
    this._assets.next([]);
  }
}

