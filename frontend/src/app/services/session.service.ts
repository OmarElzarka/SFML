import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, BehaviorSubject, interval, Subscription, timer } from 'rxjs';
import { switchMap, takeWhile, tap, catchError } from 'rxjs/operators';

export interface CreateSessionRequest {
  sourceCode: string;
}

export interface SessionResponse {
  sessionId: string;
  status: SessionStatus;
  displayUrl?: string;
  compilerOutput?: string;
  errorMessage?: string;
  createdAt: string;
}

export interface DisplayInfo {
  host: string;
  port: number;
  path: string;
}

export type SessionStatus =
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

  session$ = this._session.asObservable();
  displayInfo$ = this._displayInfo.asObservable();
  isLoading$ = this._isLoading.asObservable();

  constructor(private http: HttpClient) {}

  createSession(sourceCode: string): void {
    this._isLoading.next(true);
    this._displayInfo.next(null);

    this.http
      .post<SessionResponse>(`${this.apiUrl}/sessions`, { sourceCode })
      .subscribe({
        next: (response) => {
          this._session.next(response);
          this._isLoading.next(false);

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
            sessionId: '',
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
          // Retry after a short delay
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
            // Session may have expired
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
          this._session.next(null);
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
  }
}
