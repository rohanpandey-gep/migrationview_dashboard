import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DashboardService } from './dashboard.service';
import { DashboardResponse, SeedResponse, PrioritySection } from './models';

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
    if (!this.selectedEnvironment) return;
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

  private getTodayString(): string {
    const today = new Date();
    return today.toISOString().split('T')[0];
  }
}
