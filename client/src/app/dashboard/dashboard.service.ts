import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { DashboardResponse, SeedResponse } from './models';

@Injectable({
  providedIn: 'root'
})
export class DashboardService {
  private baseUrl = '/api/dashboard';

  constructor(private http: HttpClient) {}

  getSeed(): Observable<SeedResponse> {
    return this.http.get<SeedResponse>(`${this.baseUrl}/seed`);
  }

  getData(environment: string, date: string, authorization: string): Observable<DashboardResponse> {
    return this.http.get<DashboardResponse>(`${this.baseUrl}/data`, {
      params: { environment, date },
      headers: { 'X-Auth-Token': authorization }
    });
  }

  getQueueCount(authorization: string): Observable<any> {
    return this.http.get<any>(`${this.baseUrl}/queue`, {
      headers: { 'X-Auth-Token': authorization }
    });
  }
}
