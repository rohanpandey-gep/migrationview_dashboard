import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DashboardService } from './dashboard.service';
import { ModuleCounts, PrioritySection } from './models';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.css'
})
export class DashboardComponent implements OnInit, OnDestroy {
  environments: string[] = [];
  selectedEnvironment = '';
  selectedDate = '';
  jwtToken = '';
  queueCount = 0;
  sections: PrioritySection[] = [];
  loading = false;
  private queueInterval: any = null;

  constructor(private dashboardService: DashboardService) {}

  ngOnInit(): void {
    this.dashboardService.getSeed().subscribe(seed => {
      this.environments = seed.environments;
      this.selectedEnvironment = seed.environments[0] || '';
      this.selectedDate = this.getTodayString();
    });
  }

  ngOnDestroy(): void {
    this.stopQueuePolling();
  }

  loadData(): void {
    if (!this.selectedEnvironment || this.loading) return;
    this.loading = true;

    let token = this.jwtToken.trim();
    if (!token.toLowerCase().startsWith('bearer ')) {
      token = `Bearer ${token}`;
    }

    this.dashboardService.getData(this.selectedEnvironment, this.selectedDate, token).subscribe(data => {
      this.sections = data.sections;
      this.loading = false;
    });

    this.refreshQueue();
    this.startQueuePolling();
  }

  refreshQueue(): void {
    if (!this.jwtToken) return;
    let token = this.jwtToken.trim();
    if (!token.toLowerCase().startsWith('bearer ')) {
      token = `Bearer ${token}`;
    }
    this.dashboardService.getQueueCount(token).subscribe(data => {
      this.queueCount = data.returnValue ?? 0;
    });
  }

  private startQueuePolling(): void {
    this.stopQueuePolling();
    this.queueInterval = setInterval(() => this.refreshQueue(), 60000);
  }

  private stopQueuePolling(): void {
    if (this.queueInterval) {
      clearInterval(this.queueInterval);
      this.queueInterval = null;
    }
  }

  onEnvironmentChange(): void {}

  onDateChange(): void {}

  getRestTotal(section: PrioritySection, moduleIndex: number): number {
    return section.cards.reduce((sum, card) => sum + (card.modules[moduleIndex]?.missedQueue ?? 0), 0);
  }

  getRestPublishedTotal(section: PrioritySection, moduleIndex: number): number {
    return section.cards.reduce((sum, card) => sum + (card.modules[moduleIndex]?.published ?? 0), 0);
  }

  getRestFailedTotal(section: PrioritySection, moduleIndex: number): number {
    return section.cards.reduce((sum, card) => sum + (card.modules[moduleIndex]?.failed ?? 0), 0);
  }

  getPriorityRowStatus(row: ModuleCounts): 'healthy' | 'warning' | 'critical' {
    return this.getRowStatus(row.missedQueue, row.failed);
  }

  getPriorityRowClass(row: ModuleCounts): string {
    return `status-${this.getPriorityRowStatus(row)}`;
  }

  getRestRowStatus(section: PrioritySection, moduleIndex: number): 'healthy' | 'warning' | 'critical' {
    const missedQueue = this.getRestTotal(section, moduleIndex);
    const failed = this.getRestFailedTotal(section, moduleIndex);
    return this.getRowStatus(missedQueue, failed);
  }

  getRestRowClass(section: PrioritySection, moduleIndex: number): string {
    return `status-${this.getRestRowStatus(section, moduleIndex)}`;
  }

  private getRowStatus(missedQueue: number, failed: number): 'healthy' | 'warning' | 'critical' {
    if (failed > 0) return 'critical';
    if (missedQueue > 0) return 'warning';
    return 'healthy';
  }

  private getTodayString(): string {
    const today = new Date();
    return today.toISOString().split('T')[0];
  }
}
