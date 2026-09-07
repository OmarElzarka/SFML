import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, BehaviorSubject, tap, map } from 'rxjs';
import { SessionResponse } from './session.service';

export interface UserDto {
  id: number;
  username: string;
  createdAt: string;
  lastSeenAt: string;
}

export interface ProjectSummaryDto {
  id: number;
  userId: number;
  name: string;
  description?: string;
  createdAt: string;
  updatedAt: string;
  fileCount: number;
  assetCount: number;
}

export interface ProjectFileDto {
  id: number;
  projectId: number;
  path: string;
  content: string;
  createdAt: string;
  updatedAt: string;
}

export interface ProjectAssetDto {
  id: number;
  projectId: number;
  fileName: string;
  relativePath: string;
  size: number;
  contentType?: string;
  createdAt: string;
}

export interface ProjectDetailDto {
  id: number;
  userId: number;
  name: string;
  description?: string;
  createdAt: string;
  updatedAt: string;
  files: ProjectFileDto[];
  assets: ProjectAssetDto[];
}

export interface CreateProjectRequest {
  userId: number;
  name: string;
  description?: string;
  templateKey?: string;
}

export interface CreateFileRequest {
  path: string;
  content: string;
}

export interface UpdateFileRequest {
  content: string;
  path?: string;
}

export interface RunProjectRequest {
  sessionId?: string;
}

@Injectable({
  providedIn: 'root',
})
export class ProjectService {
  private apiUrl = '/api';

  private _currentUser = new BehaviorSubject<UserDto | null>(null);
  private _projects = new BehaviorSubject<ProjectSummaryDto[]>([]);
  private _currentProject = new BehaviorSubject<ProjectDetailDto | null>(null);
  private _activeFile = new BehaviorSubject<ProjectFileDto | null>(null);
  private _saveStatus = new BehaviorSubject<'saved' | 'saving' | 'unsaved'>('saved');

  currentUser$ = this._currentUser.asObservable();
  projects$ = this._projects.asObservable();
  currentProject$ = this._currentProject.asObservable();
  activeFile$ = this._activeFile.asObservable();
  saveStatus$ = this._saveStatus.asObservable();

  get currentUser(): UserDto | null {
    return this._currentUser.value;
  }

  get currentProject(): ProjectDetailDto | null {
    return this._currentProject.value;
  }

  get activeFile(): ProjectFileDto | null {
    return this._activeFile.value;
  }

  constructor(private http: HttpClient) {
    const savedUser = localStorage.getItem('sfml_user');
    if (savedUser) {
      try {
        const u = JSON.parse(savedUser) as UserDto;
        this._currentUser.next(u);
        this.loadProjects(u.id).subscribe((projects) => {
          const lastProjectId = localStorage.getItem('sfml_last_project_id');
          if (lastProjectId) {
            const pid = parseInt(lastProjectId, 10);
            if (projects.some((p) => p.id === pid)) {
              this.loadProject(pid).subscribe();
              return;
            }
          }
          if (projects.length > 0) {
            this.loadProject(projects[0].id).subscribe();
          }
        });
      } catch {
        localStorage.removeItem('sfml_user');
      }
    }
  }

  // ─── User ─────────────────────────────────────────────────────────────────

  login(username: string): Observable<UserDto> {
    return this.http.post<UserDto>(`${this.apiUrl}/users`, { username }).pipe(
      tap((user) => {
        this._currentUser.next(user);
        localStorage.setItem('sfml_user', JSON.stringify(user));
        this.loadProjects(user.id).subscribe((projects) => {
          if (projects.length > 0) {
            this.loadProject(projects[0].id).subscribe();
          } else {
            // Auto create starter project for new user
            this.createProject(user.id, 'My First Game', 'sprite').subscribe();
          }
        });
      })
    );
  }

  logout(): void {
    this._currentUser.next(null);
    this._currentProject.next(null);
    this._projects.next([]);
    this._activeFile.next(null);
    localStorage.removeItem('sfml_user');
    localStorage.removeItem('sfml_last_project_id');
  }

  // ─── Projects ─────────────────────────────────────────────────────────────

  loadProjects(userId: number): Observable<ProjectSummaryDto[]> {
    return this.http.get<ProjectSummaryDto[]>(`${this.apiUrl}/projects?userId=${userId}`).pipe(
      tap((projects) => this._projects.next(projects))
    );
  }

  loadProject(projectId: number): Observable<ProjectDetailDto> {
    return this.http.get<ProjectDetailDto>(`${this.apiUrl}/projects/${projectId}`).pipe(
      tap((proj) => {
        this._currentProject.next(proj);
        localStorage.setItem('sfml_last_project_id', proj.id.toString());
        // Select main.cpp by default, or the first file
        const main = proj.files.find((f) => f.path.toLowerCase() === 'main.cpp') || proj.files[0] || null;
        this._activeFile.next(main);
        this._saveStatus.next('saved');
      })
    );
  }

  createProject(userId: number, name: string, templateKey: string = 'empty'): Observable<ProjectDetailDto> {
    const req: CreateProjectRequest = { userId, name, templateKey };
    return this.http.post<ProjectDetailDto>(`${this.apiUrl}/projects`, req).pipe(
      tap((proj) => {
        this.loadProjects(userId).subscribe();
        this._currentProject.next(proj);
        localStorage.setItem('sfml_last_project_id', proj.id.toString());
        const main = proj.files.find((f) => f.path.toLowerCase() === 'main.cpp') || proj.files[0] || null;
        this._activeFile.next(main);
      })
    );
  }

  renameProject(projectId: number, name: string): Observable<ProjectDetailDto> {
    return this.http.put<ProjectDetailDto>(`${this.apiUrl}/projects/${projectId}`, { name }).pipe(
      tap((proj) => {
        this._currentProject.next(proj);
        if (this.currentUser) {
          this.loadProjects(this.currentUser.id).subscribe();
        }
      })
    );
  }

  deleteProject(projectId: number): Observable<void> {
    return this.http.delete<void>(`${this.apiUrl}/projects/${projectId}`).pipe(
      tap(() => {
        if (this.currentUser) {
          this.loadProjects(this.currentUser.id).subscribe((projects) => {
            if (this.currentProject?.id === projectId) {
              if (projects.length > 0) {
                this.loadProject(projects[0].id).subscribe();
              } else {
                this._currentProject.next(null);
                this._activeFile.next(null);
                localStorage.removeItem('sfml_last_project_id');
              }
            }
          });
        }
      })
    );
  }

  // ─── Files ────────────────────────────────────────────────────────────────

  setActiveFile(file: ProjectFileDto): void {
    this._activeFile.next(file);
    this._saveStatus.next('saved');
  }

  createFile(projectId: number, path: string, content: string = ''): Observable<ProjectFileDto> {
    return this.http.post<ProjectFileDto>(`${this.apiUrl}/projects/${projectId}/files`, { path, content }).pipe(
      tap((newFile) => {
        const proj = this._currentProject.value;
        if (proj) {
          const updatedFiles = [...proj.files, newFile];
          this._currentProject.next({ ...proj, files: updatedFiles });
        }
        this.setActiveFile(newFile);
      })
    );
  }

  updateFile(projectId: number, fileId: number, content: string): Observable<ProjectFileDto> {
    this._saveStatus.next('saving');
    return this.http.put<ProjectFileDto>(`${this.apiUrl}/projects/${projectId}/files/${fileId}`, { content }).pipe(
      tap((updated) => {
        const proj = this._currentProject.value;
        if (proj) {
          const idx = proj.files.findIndex((f) => f.id === fileId);
          if (idx >= 0) {
            const files = [...proj.files];
            files[idx] = updated;
            this._currentProject.next({ ...proj, files });
          }
        }
        if (this._activeFile.value?.id === fileId) {
          this._activeFile.next(updated);
        }
        this._saveStatus.next('saved');
      })
    );
  }

  deleteFile(projectId: number, fileId: number): Observable<void> {
    return this.http.delete<void>(`${this.apiUrl}/projects/${projectId}/files/${fileId}`).pipe(
      tap(() => {
        const proj = this._currentProject.value;
        if (proj) {
          const files = proj.files.filter((f) => f.id !== fileId);
          this._currentProject.next({ ...proj, files });
          if (this._activeFile.value?.id === fileId) {
            const nextActive = files.find((f) => f.path.toLowerCase() === 'main.cpp') || files[0] || null;
            this._activeFile.next(nextActive);
          }
        }
      })
    );
  }

  // ─── Assets ───────────────────────────────────────────────────────────────

  uploadAsset(projectId: number, file: File): Observable<ProjectAssetDto> {
    const formData = new FormData();
    formData.append('file', file, file.name);

    return this.http.post<ProjectAssetDto>(`${this.apiUrl}/projects/${projectId}/assets`, formData).pipe(
      tap((uploaded) => {
        const proj = this._currentProject.value;
        if (proj) {
          const idx = proj.assets.findIndex(
            (a) => a.fileName.toLowerCase() === uploaded.fileName.toLowerCase()
          );
          let updatedAssets: ProjectAssetDto[];
          if (idx >= 0) {
            updatedAssets = [...proj.assets];
            updatedAssets[idx] = uploaded;
          } else {
            updatedAssets = [...proj.assets, uploaded];
          }
          this._currentProject.next({ ...proj, assets: updatedAssets });
        }
      })
    );
  }

  deleteAsset(projectId: number, assetId: number): Observable<void> {
    return this.http.delete<void>(`${this.apiUrl}/projects/${projectId}/assets/${assetId}`).pipe(
      tap(() => {
        const proj = this._currentProject.value;
        if (proj) {
          const assets = proj.assets.filter((a) => a.id !== assetId);
          this._currentProject.next({ ...proj, assets });
        }
      })
    );
  }

  // ─── Execution ────────────────────────────────────────────────────────────

  runProject(projectId: number, sessionId?: string): Observable<SessionResponse> {
    return this.http.post<SessionResponse>(`${this.apiUrl}/projects/${projectId}/run`, { sessionId });
  }
}
